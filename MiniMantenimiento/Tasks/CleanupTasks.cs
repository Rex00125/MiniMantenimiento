// Tasks/CleanupTasks.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MiniMantenimiento.Tasks
{
    internal sealed class CleanupTasks
    {
        internal sealed class CleanupResult
        {
            public long FreedBytes { get; set; }
            public List<string> LogLines { get; } = new List<string>();
        }

        public CleanupResult Run(Action<string> log, CancellationToken ct)
        {
            var result = new CleanupResult();

            log("===== LIMPIEZA DE SISTEMA =====");

            var targets = new List<string>
            {
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
            };

            foreach (var path in targets)
            {
                if (ct.IsCancellationRequested)
                {
                    log("Limpieza cancelada.");
                    break;
                }

                CleanupDirectory(path, result, log);
            }

            // Papelera (opcional, segura)
            EmptyRecycleBin(result, log);

            log($"Limpieza finalizada. Espacio liberado: {FormatBytes(result.FreedBytes)}");
            log("==============================");

            return result;
        }

        // ----------------------------
        // Limpieza de carpetas
        // ----------------------------

        private static void CleanupDirectory(
            string path,
            CleanupResult result,
            Action<string> log)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    log($"[SKIP] No existe: {path}");
                    return;
                }

                long before = GetDirectorySize(path);
                log($"Analizando: {path} ({FormatBytes(before)})");

                DeleteFilesRecursive(path);

                long after = GetDirectorySize(path);
                long freed = Math.Max(0, before - after);

                result.FreedBytes += freed;

                log($"✔ Limpio: {path} | Liberado: {FormatBytes(freed)}");
            }
            catch (Exception ex)
            {
                log($"✖ Error en {path}: {ex.Message}");
            }
        }

        private static void DeleteFilesRecursive(string path)
        {
            foreach (string file in Directory.GetFiles(path))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch { /* ignorar archivos en uso */ }
            }

            foreach (string dir in Directory.GetDirectories(path))
            {
                try
                {
                    DeleteFilesRecursive(dir);
                    Directory.Delete(dir, false);
                }
                catch { /* ignorar carpetas bloqueadas */ }
            }
        }

        // ----------------------------
        // Papelera de reciclaje
        // ----------------------------

        private static void EmptyRecycleBin(
            CleanupResult result,
            Action<string> log)
        {
            try
            {
                long before = GetRecycleBinSize();
                SHEmptyRecycleBin(IntPtr.Zero, null,
                    RecycleFlags.SHERB_NOCONFIRMATION |
                    RecycleFlags.SHERB_NOPROGRESSUI |
                    RecycleFlags.SHERB_NOSOUND);

                long after = GetRecycleBinSize();
                long freed = Math.Max(0, before - after);

                result.FreedBytes += freed;
                log($"✔ Papelera vaciada | Liberado: {FormatBytes(freed)}");
            }
            catch
            {
                log("Papelera: no se pudo vaciar (sin permisos o no disponible)");
            }
        }

        // ----------------------------
        // Utilidades
        // ----------------------------

        private static long GetDirectorySize(string path)
        {
            try
            {
                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(f =>
                    {
                        try { return new FileInfo(f).Length; }
                        catch { return 0; }
                    });
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatBytes(long bytes)
        {
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (b >= 1024 && u < units.Length - 1)
            {
                b /= 1024;
                u++;
            }
            return $"{b:0.##} {units[u]}";
        }

        // ----------------------------
        // WinAPI Papelera
        // ----------------------------

        [DllImport("shell32.dll")]
        private static extern int SHEmptyRecycleBin(
            IntPtr hwnd,
            string pszRootPath,
            RecycleFlags dwFlags);

        private enum RecycleFlags
        {
            SHERB_NOCONFIRMATION = 0x00000001,
            SHERB_NOPROGRESSUI = 0x00000002,
            SHERB_NOSOUND = 0x00000004
        }

        private static long GetRecycleBinSize()
        {
            // No hay API directa simple, así que lo tratamos como estimado 0
            // (sirve solo para evitar romper el flujo)
            return 0;
        }
    }
}
