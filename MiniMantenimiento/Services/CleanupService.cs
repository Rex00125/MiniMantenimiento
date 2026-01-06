// Services/CleanupService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace MiniMantenimiento.Services
{
    internal sealed class CleanupService
    {
        public sealed class CleanupResult
        {
            public long FreedBytes;
            public List<string> LogLines = new List<string>();
        }

        public CleanupResult RunCleanup()
        {
            var res = new CleanupResult();

            string userTemp = Path.GetTempPath();
            string windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            var targets = new List<string> { userTemp, windowsTemp };

            res.LogLines.Add("===== LIMPIEZA =====");
            res.LogLines.Add("Objetivos:");
            foreach (var t in targets) res.LogLines.Add("  - " + t);

            long totalEstimatedFreed = 0;

            foreach (string path in targets)
            {
                long beforeBytes = TryGetDirectorySize(path, out string sizeNote);
                res.LogLines.Add($"Analizando: {path} | Tamaño aprox: {FormatBytes((ulong)beforeBytes)}" +
                                 (string.IsNullOrWhiteSpace(sizeNote) ? "" : " (" + sizeNote + ")"));

                long deletedBytes = SafeDeleteDirectoryContents(path, res.LogLines);
                totalEstimatedFreed += deletedBytes;

                res.LogLines.Add($"  -> Borrado aprox en {path}: {FormatBytes((ulong)deletedBytes)}");
            }

            res.FreedBytes = totalEstimatedFreed;
            res.LogLines.Add("====================");

            return res;
        }

        public bool TryEmptyRecycleBin(out string message, out int hresult)
        {
            message = null;
            hresult = 0;

            try
            {
                int hr = SHEmptyRecycleBin(IntPtr.Zero, null,
                    RecycleFlags.SHERB_NOCONFIRMATION | RecycleFlags.SHERB_NOPROGRESSUI | RecycleFlags.SHERB_NOSOUND);

                hresult = hr;
                if (hr == 0) return true;

                message = "No se pudo vaciar (HRESULT: 0x" + hr.ToString("X8") + ")";
                return false;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        private long SafeDeleteDirectoryContents(string path, List<string> logLines)
        {
            long deletedBytes = 0;

            try
            {
                if (!Directory.Exists(path))
                {
                    logLines.Add("  [WARN] No existe: " + path);
                    return 0;
                }

                // Archivos directos
                string[] files = null;
                try { files = Directory.GetFiles(path); }
                catch (Exception ex) { logLines.Add("  [WARN] No se pudieron listar archivos: " + ex.Message); }

                if (files != null)
                {
                    foreach (var file in files)
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            long len = fi.Exists ? fi.Length : 0;

                            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                            File.Delete(file);

                            deletedBytes += len;
                        }
                        catch (Exception ex)
                        {
                            logLines.Add("  [WARN] No se pudo borrar archivo: " + file + " | " + ex.Message);
                        }
                    }
                }

                // Subcarpetas
                string[] dirs = null;
                try { dirs = Directory.GetDirectories(path); }
                catch (Exception ex) { logLines.Add("  [WARN] No se pudieron listar carpetas: " + ex.Message); }

                if (dirs != null)
                {
                    foreach (var dir in dirs)
                        deletedBytes += SafeDeleteDirectoryTree(dir, logLines);
                }
            }
            catch (Exception ex)
            {
                logLines.Add("  [WARN] Error general en " + path + ": " + ex.Message);
            }

            return deletedBytes;
        }

        private long SafeDeleteDirectoryTree(string dir, List<string> logLines)
        {
            long bytesDeleted = 0;

            try
            {
                if (!Directory.Exists(dir))
                    return 0;

                // ✅ Evita junctions/symlinks (reparse points)
                try
                {
                    var di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        logLines.Add("  [WARN] Saltando reparse point: " + dir);
                        return 0;
                    }
                }
                catch { }

                long approxSize = TryGetDirectorySize(dir, out _);

                // Normaliza atributos (si se puede)
                try
                {
                    foreach (var f in SafeEnumerateFiles(dir))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                }
                catch { }

                Directory.Delete(dir, true);
                bytesDeleted += approxSize;
            }
            catch (Exception ex)
            {
                logLines.Add("  [WARN] No se pudo borrar carpeta: " + dir + " | " + ex.Message);

                // fallback: intenta vaciar por dentro
                bytesDeleted += SafeDeleteDirectoryContents(dir, logLines);
                try { Directory.Delete(dir, false); } catch { }
            }

            return bytesDeleted;
        }

        private long TryGetDirectorySize(string path, out string note)
        {
            note = null;

            try
            {
                if (!Directory.Exists(path)) { note = "no existe"; return 0; }

                // ✅ Evita junctions/symlinks
                try
                {
                    var di = new DirectoryInfo(path);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        note = "reparse point (omitido)";
                        return 0;
                    }
                }
                catch { }

                long size = 0;
                foreach (var file in SafeEnumerateFiles(path))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.Exists) size += fi.Length;
                    }
                    catch { }
                }
                return size;
            }
            catch (Exception ex)
            {
                note = "no se pudo calcular: " + ex.Message;
                return 0;
            }
        }

        // Enumeración de archivos tolerante a permisos/carpetas problemáticas
        private IEnumerable<string> SafeEnumerateFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                // Evita reparse points
                try
                {
                    var di = new DirectoryInfo(current);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch { continue; }

                string[] files = null;
                try { files = Directory.GetFiles(current); } catch { }
                if (files != null)
                {
                    foreach (var f in files) yield return f;
                }

                string[] dirs = null;
                try { dirs = Directory.GetDirectories(current); } catch { }
                if (dirs != null)
                {
                    foreach (var d in dirs) stack.Push(d);
                }
            }
        }

        private static string FormatBytes(ulong bytes)
        {
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
            return string.Format("{0:0.##} {1}", b, units[u]);
        }

        [Flags]
        private enum RecycleFlags : uint
        {
            SHERB_NOCONFIRMATION = 0x00000001,
            SHERB_NOPROGRESSUI = 0x00000002,
            SHERB_NOSOUND = 0x00000004
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, RecycleFlags dwFlags);
    }
}
