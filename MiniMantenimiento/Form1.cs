// Form1.cs
// Requiere referencia: System.Management
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MiniMantenimiento.Reports;
using System.Threading;
using MiniMantenimiento.Tasks;

namespace MiniMantenimiento
{
    public partial class Form1 : Form
    {

        private readonly System.Text.StringBuilder _log = new System.Text.StringBuilder();


        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Cargar defaults del visor
            clbLogs.Items.Clear();
            clbLogs.Items.Add("System", true);
            clbLogs.Items.Add("Application", true);
            clbLogs.Items.Add("Setup", false);
            clbLogs.Items.Add("Security", false);

            clbLevels.Items.Clear();
            clbLevels.Items.Add("Crítico", true);
            clbLevels.Items.Add("Error", true);
            clbLevels.Items.Add("Advertencia", false);
            clbLevels.Items.Add("Información", false);
            clbLevels.Items.Add("Detallado (Verbose)", false);

            cmbRange.Items.Clear();
            cmbRange.Items.Add("Últimas 24 horas");
            cmbRange.Items.Add("Últimos 7 días");
            cmbRange.Items.Add("Últimos 30 días");
            cmbRange.SelectedIndex = 1;

            // Estado admin
            bool isAdmin = IsRunningAsAdmin();
            lblAdminStatus.Text = isAdmin
                ? "Estado: Ejecutando como Administrador ✅ (reparaciones avanzadas habilitadas)"
                : "Estado: Sin permisos de Administrador ⚠️ (algunas reparaciones se deshabilitarán)";

            btnSfc.Enabled = isAdmin;
            btnDism.Enabled = isAdmin;
            btnWinUpdateFix.Enabled = isAdmin;
            btnDriversRescan.Enabled = isAdmin;
            btnNetReset.Enabled = isAdmin; // safe por ahora (lo puedes cambiar luego)

            Log("App iniciada.");
        }

        // =========================
        // Logger
        // =========================
        private void Log(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            if (this.InvokeRequired)
            {
                try { this.BeginInvoke((Action)(() => Log(message))); }
                catch { }
                return;
            }

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            _log.AppendLine(line);
            txtLog.AppendText(line + Environment.NewLine);
        }


        private static bool IsRunningAsAdmin()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private string FormatBytes(ulong bytes)
        {
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int u = 0;

            while (b >= 1024 && u < units.Length - 1)
            {
                b /= 1024;
                u++;
            }

            return string.Format("{0:0.##} {1}", b, units[u]);
        }

        private string FormatUptime()
        {
            try
            {
                long ms = (long)(uint)Environment.TickCount;
                var ts = TimeSpan.FromMilliseconds(ms);

                if (ts.TotalDays >= 1)
                    return string.Format("{0}d {1}h {2}m", ts.Days, ts.Hours, ts.Minutes);
                if (ts.TotalHours >= 1)
                    return string.Format("{0}h {1}m", (int)ts.TotalHours, ts.Minutes);
                return string.Format("{0}m {1}s", ts.Minutes, ts.Seconds);
            }
            catch
            {
                return "(no disponible)";
            }
        }

        // =========================
        // Chequeo rápido
        // =========================
        private void btnHealthCheck_Click(object sender, EventArgs e)
        {
            btnHealthCheck.Enabled = false;
            pb.Style = ProgressBarStyle.Marquee;

            Log("Chequeo rápido: iniciando...");

            Task.Run(() =>
            {
                try
                {
                    var lines = BuildHealthCheckLines();

                    this.BeginInvoke((Action)(() =>
                    {
                        foreach (var line in lines)
                            Log(line);

                        Log("Chequeo rápido: listo ✅");
                    }));
                }
                catch (Exception ex)
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        Log("Chequeo rápido: error ❌ " + ex.Message);
                        MessageBox.Show(this, "Error en Chequeo rápido:\n\n" + ex, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        pb.Style = ProgressBarStyle.Blocks;
                        btnHealthCheck.Enabled = true;
                    }));
                }
            });
        }

        private List<string> BuildHealthCheckLines()
        {
            var lines = new List<string>();

            lines.Add("===== CHEQUEO RÁPIDO =====");
            lines.Add("Equipo: " + Environment.MachineName);
            lines.Add("Usuario: " + Environment.UserName);
            lines.Add("Número de serie: " + GetSerialNumber());
            lines.Add("Windows: " + GetWindowsFriendlyName());
            lines.Add("Arquitectura: " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
            lines.Add("Uptime: " + FormatUptime());

            string cpu = GetCpuName();
            if (!string.IsNullOrWhiteSpace(cpu))
                lines.Add("CPU: " + cpu);

            var ram = GetRamInfo();
            if (ram.TotalBytes > 0)
            {
                lines.Add(string.Format("RAM: Total {0} | Disponible {1}",
                    FormatBytes(ram.TotalBytes),
                    FormatBytes(ram.AvailableBytes)));
            }

            lines.Add("Discos:");
            foreach (var d in GetDiskInfo())
                lines.Add("  - " + d);

            lines.Add("Red:");
            foreach (var n in GetNetworkInfo())
                lines.Add("  - " + n);

            lines.Add("==========================");

            return lines;
        }

        private string GetWindowsFriendlyName()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Caption, Version FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject os in searcher.Get())
                    {
                        string caption = (os["Caption"] != null) ? os["Caption"].ToString().Trim() : "";
                        string version = (os["Version"] != null) ? os["Version"].ToString().Trim() : "";
                        if (!string.IsNullOrWhiteSpace(caption))
                            return caption + (string.IsNullOrWhiteSpace(version) ? "" : " (v" + version + ")");
                    }
                }
            }
            catch { }

            return Environment.OSVersion.VersionString;
        }

        private string GetSerialNumber()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                {
                    foreach (ManagementObject bios in searcher.Get())
                    {
                        string serial = bios["SerialNumber"] as string;
                        if (!string.IsNullOrWhiteSpace(serial))
                        {
                            serial = serial.Trim();
                            if (serial.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
                                serial.IndexOf("O.E.M", StringComparison.OrdinalIgnoreCase) >= 0)
                                return "(no definido por el fabricante)";
                            return serial;
                        }
                    }
                }
            }
            catch { }

            return "(no disponible)";
        }

        private string GetCpuName()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject cpu in searcher.Get())
                    {
                        var name = cpu["Name"] as string;
                        if (!string.IsNullOrWhiteSpace(name))
                            return name.Trim();
                    }
                }
            }
            catch { }
            return "";
        }

        private (ulong TotalBytes, ulong AvailableBytes) GetRamInfo()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject os in searcher.Get())
                    {
                        ulong totalKb = 0;
                        ulong freeKb = 0;

                        if (os["TotalVisibleMemorySize"] != null)
                            totalKb = Convert.ToUInt64(os["TotalVisibleMemorySize"]);

                        if (os["FreePhysicalMemory"] != null)
                            freeKb = Convert.ToUInt64(os["FreePhysicalMemory"]);

                        return (totalKb * 1024UL, freeKb * 1024UL);
                    }
                }
            }
            catch { }

            return (0, 0);
        }

        private IEnumerable<string> GetDiskInfo()
        {
            var list = new List<string>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;

                    long total = drive.TotalSize;
                    long free = drive.AvailableFreeSpace;
                    double pctFree = total > 0 ? (free * 100.0 / total) : 0;

                    list.Add(string.Format("{0} ({1}) - Libre: {2} / {3} ({4:0.0}% libre)",
                        drive.Name,
                        drive.DriveType,
                        FormatBytes((ulong)free),
                        FormatBytes((ulong)total),
                        pctFree));
                }
            }
            catch (Exception ex)
            {
                list.Add("Discos: error al leer (" + ex.Message + ")");
            }

            if (list.Count == 0)
                list.Add("No se pudieron enumerar discos.");

            return list;
        }

        private IEnumerable<string> GetNetworkInfo()
        {
            var list = new List<string>();

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var ipProps = ni.GetIPProperties();

                    var ipv4s = ipProps.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString())
                        .ToList();

                    if (ipv4s.Count == 0) continue;

                    var gw = ipProps.GatewayAddresses
                        .Select(g => g.Address)
                        .Where(a => a != null && a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .FirstOrDefault();

                    var dns = ipProps.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .Take(3)
                        .ToList();

                    string line = ni.Name + " | IP: " + string.Join(", ", ipv4s);
                    if (!string.IsNullOrWhiteSpace(gw)) line += " | GW: " + gw;
                    if (dns.Count > 0) line += " | DNS: " + string.Join(", ", dns);

                    list.Add(line);
                }
            }
            catch (Exception ex)
            {
                list.Add("Red: error al leer (" + ex.Message + ")");
            }

            if (list.Count == 0)
                list.Add("No se detectó interfaz activa con IPv4.");

            return list;
        }

        // =========================
        // Limpieza (temporales + papelera)
        // =========================
        private void btnCleanup_Click(object sender, EventArgs e)
        {
            btnCleanup.Enabled = false;
            pb.Style = ProgressBarStyle.Marquee;

            bool emptyBin = (chkEmptyRecycleBin != null && chkEmptyRecycleBin.Checked);

            Log("Limpieza: iniciando...");
            if (emptyBin) Log("Limpieza: Papelera marcada para vaciar.");

            Task.Run(() =>
            {
                CleanupResult result = null;
                Exception caught = null;

                try
                {
                    // 1) Temporales en background
                    result = RunCleanup(false); // <-- OJO: aquí NO vaciamos papelera
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                this.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (caught != null)
                        {
                            Log("Limpieza: error ❌ " + caught.Message);
                            MessageBox.Show(this, "Error al limpiar temporales:\n\n" + caught, "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        // Log de temporales
                        foreach (var line in result.LogLines)
                            Log(line);

                        // 2) Papelera en UI thread
                        if (emptyBin)
                        {
                            Log("Vaciando Papelera...");
                            string msg;
                            int hr;
                            bool ok = TryEmptyRecycleBin(out msg, out hr);
                            Log(ok ? "  -> Papelera vaciada ✅" : ("  -> Papelera: " + (msg ?? "falló") + " (hr=0x" + hr.ToString("X8") + ")"));
                        }

                        Log(string.Format("Limpieza: liberado aprox. {0} ✅", FormatBytes((ulong)result.FreedBytes)));
                    }
                    finally
                    {
                        pb.Style = ProgressBarStyle.Blocks;
                        btnCleanup.Enabled = true;
                    }
                }));
            });
        }



        private class CleanupResult
        {
            public long FreedBytes;
            public List<string> LogLines = new List<string>();
        }

        private CleanupResult RunCleanup(bool _)
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
                res.LogLines.Add(string.Format("Analizando: {0} | Tamaño aprox: {1}{2}",
                    path,
                    FormatBytes((ulong)beforeBytes),
                    string.IsNullOrWhiteSpace(sizeNote) ? "" : " (" + sizeNote + ")"));

                long deletedBytes = SafeDeleteDirectoryContents(path, res.LogLines);
                totalEstimatedFreed += deletedBytes;

                res.LogLines.Add(string.Format("  -> Borrado aprox en {0}: {1}", path, FormatBytes((ulong)deletedBytes)));
            }



            res.FreedBytes = totalEstimatedFreed;
            res.LogLines.Add("====================");

            return res;
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
                long approxSize = TryGetDirectorySize(dir, out _);

                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
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
                if (!Directory.Exists(path))
                {
                    note = "no existe";
                    return 0;
                }

                long size = 0;

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
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

        // Papelera (WinAPI)
        [Flags]
        private enum RecycleFlags : uint
        {
            SHERB_NOCONFIRMATION = 0x00000001,
            SHERB_NOPROGRESSUI = 0x00000002,
            SHERB_NOSOUND = 0x00000004
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, RecycleFlags dwFlags);

        private bool TryEmptyRecycleBin(out string message, out int hresult)
        {
            message = null;
            hresult = 0;

            try
            {
                int hr = SHEmptyRecycleBin(
                    IntPtr.Zero,   // <-- NO uses this.Handle desde background
                    null,
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



        // =========================
        // Placeholders (siguiente)
        // =========================
        private void btnNetReset_Click(object sender, EventArgs e)
        {
            var dr = MessageBox.Show(this,
                "Esto reiniciará componentes de red (DNS / Winsock / IP).\n" +
                "Puede interrumpir la conexión temporalmente.\n\n" +
                "¿Deseas continuar?",
                "Reset de red",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (dr != DialogResult.Yes)
                return;

            btnNetReset.Enabled = false;
            pb.Style = ProgressBarStyle.Marquee;

            Log("Reset de red: iniciando...");

            Task.Run(() =>
            {
                Exception caught = null;
                var steps = new List<Tuple<string, CmdResult>>();

                // OEM codepage del sistema (MX/ES normalmente 850)
                int oemCp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                string chcp = "chcp " + oemCp + " >nul & ";

                string ipResetLogPath = Path.Combine(Path.GetTempPath(), "ipreset.log");

                try
                {
                    const int T = 120000; // 2 minutos por comando

                    steps.Add(Tuple.Create(
                        "ipconfig /flushdns",
                        RunCommand("cmd.exe", "/c " + chcp + "ipconfig /flushdns", T)));

                    steps.Add(Tuple.Create(
                        "netsh winsock reset",
                        RunCommand("cmd.exe", "/c " + chcp + "netsh winsock reset", T)));

                    steps.Add(Tuple.Create(
                        "netsh int ipv4 reset",
                        RunCommand("cmd.exe", "/c " + chcp + "netsh int ipv4 reset", T)));

                    steps.Add(Tuple.Create(
                        "netsh int ipv6 reset",
                        RunCommand("cmd.exe", "/c " + chcp + "netsh int ipv6 reset", T)));

                    steps.Add(Tuple.Create(
                        "netsh int ip reset (log)",
                        RunCommand("cmd.exe", "/c " + chcp + "netsh int ip reset \"" + ipResetLogPath + "\"", T)));

                    steps.Add(Tuple.Create(
                        "ipconfig /release",
                        RunCommand("cmd.exe", "/c " + chcp + "ipconfig /release", T)));

                    steps.Add(Tuple.Create(
                        "ipconfig /renew",
                        RunCommand("cmd.exe", "/c " + chcp + "ipconfig /renew", T)));
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                this.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (caught != null)
                        {
                            Log("Reset de red: error ❌ " + caught.Message);
                            MessageBox.Show(this,
                                "Error durante el Reset de red:\n\n" + caught,
                                "Error",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            return;
                        }

                        bool anyPartial = false;
                        bool anyCritical = false;

                        foreach (var s in steps)
                        {
                            LogCmd(s.Item1, s.Item2);

                            if (s.Item2.ExitCode != 0)
                            {
                                if (IsPartialSuccess(s.Item2))
                                    anyPartial = true;
                                else
                                    anyCritical = true;
                            }
                        }

                        Log("Reset de red: terminado.");

                        if (anyCritical)
                        {
                            Log("Resultado: ⚠️ Se detectaron errores críticos en uno o más pasos.");
                            MessageBox.Show(this,
                                "El Reset de red finalizó con algunos errores.\n\n" +
                                "Revisa el log para más detalles.\n" +
                                "Se recomienda reiniciar el equipo.",
                                "Reset de red",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                        else if (anyPartial)
                        {
                            Log("Resultado: ⚠️ Éxito parcial con advertencias (Acceso denegado en algunos componentes).");
                            Log("Nota: Reinicia el equipo para aplicar completamente los cambios.");
                            Log("Log adicional (si se generó): " + ipResetLogPath);

                            MessageBox.Show(this,
                                "Reset de red completado con advertencias.\n\n" +
                                "Algunos componentes no pudieron restablecerse por completo,\n" +
                                "pero los cambios principales se aplicaron.\n\n" +
                                "Reinicia el equipo para finalizar.",
                                "Reset de red",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                        else
                        {
                            Log("Resultado: ✅ Reset de red exitoso.");
                            Log("Nota: Se recomienda reiniciar el equipo para aplicar completamente los cambios.");

                            MessageBox.Show(this,
                                "Reset de red completado correctamente.\n\n" +
                                "Reinicia el equipo para aplicar todos los cambios.",
                                "Reset de red",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                    }
                    finally
                    {
                        pb.Style = ProgressBarStyle.Blocks;
                        btnNetReset.Enabled = true;
                    }
                }));
            });


        }


        //*REINICIO DE REDES HELPER*//


        private class CmdResult
        {
            public int ExitCode;
            public string StdOut;
            public string StdErr;
        }

        private CmdResult RunCommand(string fileName, string arguments, int timeoutMs)
        {
            // OEM del sistema (en MX/ES casi siempre 850)
            int oemCp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            Encoding oemEnc = Encoding.GetEncoding(oemCp);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = oemEnc,
                StandardErrorEncoding = oemEnc
            };

            using (var p = new Process())
            {
                p.StartInfo = psi;

                var sbOut = new StringBuilder();
                var sbErr = new StringBuilder();

                p.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                bool exited = p.WaitForExit(timeoutMs);
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    return new CmdResult
                    {
                        ExitCode = -1,
                        StdOut = sbOut.ToString(),
                        StdErr = "TIMEOUT: El comando tardó demasiado y se detuvo."
                    };
                }

                return new CmdResult
                {
                    ExitCode = p.ExitCode,
                    StdOut = sbOut.ToString(),
                    StdErr = sbErr.ToString()
                };
            }
        }



        private bool IsPartialSuccess(CmdResult r)
        {
            if (r == null) return false;
            if (r.ExitCode == 0) return false;

            string text = (r.StdOut ?? "") + "\n" + (r.StdErr ?? "");
            text = text.ToLowerInvariant();

            return text.Contains("access is denied") ||
                   text.Contains("acceso denegado");
        }



        private void LogCmd(string title, CmdResult r)
        {
            bool partial = IsPartialSuccess(r);

            Log("---- " + title + " ----");
            Log("ExitCode: " + r.ExitCode + (partial ? " (ÉXITO PARCIAL / advertencias)" : ""));

            if (!string.IsNullOrWhiteSpace(r.StdOut))
            {
                foreach (var line in r.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    Log(line);
            }

            if (!string.IsNullOrWhiteSpace(r.StdErr))
            {
                Log(partial ? "[stderr - advertencias]" : "[stderr]");
                foreach (var line in r.StdErr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    Log(line);
            }

            Log("------------------------");
        }

        private async void btnSfc_Click(object sender, EventArgs e)
        {
            btnSfc.Enabled = false;
            var oldStyle = pb.Style;
            pb.Style = ProgressBarStyle.Marquee;

            var isAdmin = IsRunningAsAdmin();
            var cts = new CancellationTokenSource();

            try
            {
                var start = DateTime.Now;
                Log("SFC: iniciando...");

                var result = await SfcTask.RunAsync(isAdmin, Log, cts.Token);

                Log("SFC: duración total: " + result.Duration);

                var icon = MessageBoxIcon.Information;
                if (result.ResultOutcome == SfcTask.Outcome.Fail_NotElevated ||
                    result.ResultOutcome == SfcTask.Outcome.Fail_TrustedInstaller ||
                    result.ResultOutcome == SfcTask.Outcome.Fail_Unknown)
                    icon = MessageBoxIcon.Warning;

                MessageBox.Show(this,
                    "SFC finalizó\n\n" + (result.HumanSummary ?? "Revisa el log para el detalle."),
                    "SFC",
                    MessageBoxButtons.OK,
                    icon);
            }
            catch (OperationCanceledException)
            {
                Log("SFC: cancelado.");
                MessageBox.Show(this, "SFC fue cancelado.", "SFC", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("SFC: ERROR: " + ex.Message);
                MessageBox.Show(this, "Error en SFC:\n" + ex.Message, "SFC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                pb.Style = oldStyle;
                btnSfc.Enabled = true;
            }
        }

        private async void btnDism_Click(object sender, EventArgs e)
        {
            btnDism.Enabled = false;
            var oldStyle = pb.Style;
            pb.Style = ProgressBarStyle.Marquee;

            var isAdmin = IsRunningAsAdmin();
            var cts = new CancellationTokenSource();

            try
            {
                Log("DISM: iniciando...");
                var result = await DismTask.RunAsync(isAdmin, Log, cts.Token);

                Log("DISM: duración total: " + result.Duration);

                var icon = (result.ResultOutcome == DismTask.Outcome.Ok) ? MessageBoxIcon.Information : MessageBoxIcon.Warning;

                MessageBox.Show(this,
                    "DISM finalizó\n\n" + (result.HumanSummary ?? "Revisa el log para el detalle."),
                    "DISM",
                    MessageBoxButtons.OK,
                    icon);
            }
            catch (OperationCanceledException)
            {
                Log("DISM: cancelado.");
                MessageBox.Show(this, "DISM fue cancelado.", "DISM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("DISM: ERROR: " + ex.Message);
                MessageBox.Show(this, "Error en DISM:\n" + ex.Message, "DISM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                pb.Style = oldStyle;
                btnDism.Enabled = true;
            }
        }
        private void btnWinUpdateFix_Click(object sender, EventArgs e) => Log("Reparar Windows Update: (pendiente de implementar)");
        private async void btnDriversRescan_Click(object sender, EventArgs e)
        {
            btnDriversRescan.Enabled = false;
            var oldStyle = pb.Style;
            pb.Style = ProgressBarStyle.Marquee;

            var isAdmin = IsRunningAsAdmin();
            var cts = new CancellationTokenSource();

            try
            {
                Log("Drivers: iniciando...");
                var result = await DriversRescanTask.RunAsync(isAdmin, Log, cts.Token);

                Log("Drivers: duración total: " + result.Duration);

                var icon = (result.ResultOutcome == DriversRescanTask.Outcome.Ok) ? MessageBoxIcon.Information : MessageBoxIcon.Warning;

                MessageBox.Show(this,
                    "Drivers finalizó\n\n" + (result.HumanSummary ?? "Revisa el log para el detalle."),
                    "Drivers",
                    MessageBoxButtons.OK,
                    icon);
            }
            catch (OperationCanceledException)
            {
                Log("Drivers: cancelado.");
                MessageBox.Show(this, "Drivers fue cancelado.", "Drivers", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("Drivers: ERROR: " + ex.Message);
                MessageBox.Show(this, "Error en Drivers:\n" + ex.Message, "Drivers", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                pb.Style = oldStyle;
                btnDriversRescan.Enabled = true;
            }
        }
        private void btnEventReport_Click(object sender, EventArgs e)
        {
            // 1) Leer UI
            var selectedLogs = clbLogs.CheckedItems.Cast<object>().Select(x => x.ToString()).ToList();

            var selectedLevelsText = clbLevels.CheckedItems.Cast<object>().Select(x => x.ToString()).ToList();
            var levels = new List<EventLogReport.Level>();

            foreach (var t in selectedLevelsText)
            {
                // mapeo a enum
                if (t.IndexOf("Crítico", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Critical);
                else if (t.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Error);
                else if (t.IndexOf("Advertencia", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Warning);
                else if (t.IndexOf("Información", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Information);
                else if (t.IndexOf("Detallado", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Verbose", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Verbose);
            }

            if (selectedLogs.Count == 0)
            {
                MessageBox.Show(this, "Selecciona al menos un log.", "Event Report", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (levels.Count == 0)
            {
                MessageBox.Show(this, "Selecciona al menos un nivel.", "Event Report", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int maxEvents = (int)nudMaxEvents.Value;
            bool fullMsg = chkFullMessage.Checked;

            // 2) Rango de tiempo
            int days = 7;
            string range = (cmbRange.SelectedItem != null ? cmbRange.SelectedItem.ToString() : "Últimos 7 días");
            if (range.IndexOf("24", StringComparison.OrdinalIgnoreCase) >= 0) days = 1;
            else if (range.IndexOf("30", StringComparison.OrdinalIgnoreCase) >= 0) days = 30;
            else days = 7;

            DateTime fromUtc = DateTime.UtcNow.AddDays(-days);

            // 3) Alerta masiva (advertencia/info/verbose)
            bool massive = levels.Contains(EventLogReport.Level.Warning) ||
                          levels.Contains(EventLogReport.Level.Information) ||
                          levels.Contains(EventLogReport.Level.Verbose);

            if (massive)
            {
                var dr = MessageBox.Show(this,
                    "Seleccionaste niveles que suelen generar MUCHOS eventos (Advertencia/Información/Detallado).\n\n" +
                    "Esto puede tardar y producir un reporte grande.\n\n" +
                    "¿Deseas continuar?",
                    "Aviso (volumen alto)",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (dr != DialogResult.Yes)
                    return;
            }

            // 4) Elegir archivo destino (TXT o CSV)
            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "Guardar reporte";
                sfd.Filter = "Reporte TXT (*.txt)|*.txt|Reporte CSV (*.csv)|*.csv";
                sfd.FileName = "EventReport_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                if (sfd.ShowDialog(this) != DialogResult.OK)
                    return;

                string path = sfd.FileName;
                bool exportCsv = (sfd.FilterIndex == 2) || path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

                // 5) Correr en background
                btnEventReport.Enabled = false;
                pb.Style = ProgressBarStyle.Marquee;

                Log("EventReport: iniciando...");
                Log("Logs: " + string.Join(", ", selectedLogs));
                Log("Niveles: " + string.Join(", ", levels.Select(l => l.ToString())));
                Log("Rango: " + range + " | Desde (UTC): " + fromUtc.ToString("o"));
                Log("Máx eventos: " + maxEvents + " | Mensaje completo: " + (fullMsg ? "Sí" : "No"));

                Task.Run(() =>
                {
                    Exception caught = null;
                    EventLogReport.Result result = null;

                    try
                    {
                        var opt = new EventLogReport.Options
                        {
                            LogNames = selectedLogs,
                            Levels = levels,
                            FromUtc = fromUtc,
                            MaxEvents = maxEvents,
                            IncludeFullMessage = fullMsg
                        };

                        result = EventLogReport.Generate(opt, CancellationToken.None, (count) =>
                        {
                            // opcional: podrías actualizar un label de "count"
                            // aquí lo dejamos silencioso para no spamear UI.
                        });

                        if (exportCsv)
                            EventLogReport.ExportCsv(result, path);
                        else
                            EventLogReport.ExportTxt(result, path);
                    }
                    catch (Exception ex)
                    {
                        caught = ex;
                    }

                    this.BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            if (caught != null)
                            {
                                Log("EventReport: error ❌ " + caught.Message);
                                MessageBox.Show(this, "Error generando reporte:\n\n" + caught, "Event Report",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                                return;
                            }

                            if (result != null && result.Warnings.Count > 0)
                            {
                                foreach (var w in result.Warnings)
                                    Log("EventReport [WARN] " + w);
                            }

                            Log("EventReport: listo ✅");
                            Log("Guardado en: " + path);
                            Log("Eventos exportados: " + (result != null ? result.Entries.Count.ToString() : "0"));

                            MessageBox.Show(this,
                                "Reporte generado.\n\n" +
                                "Archivo: " + path + "\n" +
                                "Eventos: " + (result != null ? result.Entries.Count.ToString() : "0"),
                                "Event Report",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                        finally
                        {
                            pb.Style = ProgressBarStyle.Blocks;
                            btnEventReport.Enabled = true;
                        }
                    }));
                });
            }
        }


        private void btnExportLog_Click(object sender, EventArgs e)
        {
            try
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Title = "Guardar log";
                    sfd.Filter = "Archivo de texto (*.txt)|*.txt";
                    sfd.FileName = "Log_MiniMantenimiento_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt";

                    if (sfd.ShowDialog(this) != DialogResult.OK)
                        return;

                    File.WriteAllText(sfd.FileName, _log.ToString(), Encoding.UTF8);
                    Log("Log exportado: " + sfd.FileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo exportar el log.\n\n" + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
