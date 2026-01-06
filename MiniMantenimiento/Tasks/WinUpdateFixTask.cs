// Tasks/WinUpdateFixTask.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniMantenimiento.Tasks
{
    internal static class WinUpdateFixTask
    {
        internal enum WinUpdateOutcome
        {
            Ok,
            Fail_NotElevated,
            Fail_CommandError,
            Fail_Canceled,
            Fail_Exception
        }

        internal sealed class WinUpdateResult
        {
            public WinUpdateOutcome Outcome { get; set; }
            public int ExitCode { get; set; }
            public string HumanSummary { get; set; }
            public TimeSpan Duration { get; set; }

            public string ServicesSnapshotBefore { get; set; }
            public string ServicesSnapshotAfter { get; set; }
        }

        internal static async Task<WinUpdateResult> RunAsync(
            bool isAdmin,
            Action<string> log,
            CancellationToken ct,
            bool deepMode)
        {
            var sw = Stopwatch.StartNew();

            var result = new WinUpdateResult
            {
                Outcome = WinUpdateOutcome.Fail_Exception,
                ExitCode = -1,
                HumanSummary = "Sin resumen."
            };

            try
            {
                if (!isAdmin)
                {
                    result.Outcome = WinUpdateOutcome.Fail_NotElevated;
                    result.HumanSummary = "Este módulo requiere permisos de Administrador.";
                    return result;
                }

                log("WinUpdate: iniciando reparación de Windows Update...");
                log("WinUpdate: modo " + (deepMode ? "PROFUNDO" : "NORMAL"));

                ct.ThrowIfCancellationRequested();

                result.ServicesSnapshotBefore = await GetServicesSnapshotAsync(log, ct).ConfigureAwait(false);

                if (deepMode)
                {
                    log("---- Diagnóstico ----");

                    await RunAndLogAsync("dism /online /cleanup-image /checkhealth", log, ct, true).ConfigureAwait(false);
                    await RunAndLogAsync("dism /online /cleanup-image /scanhealth", log, ct, true).ConfigureAwait(false);

                    // 50 eventos recientes del canal Operational
                    await RunAndLogAsync(
                        "wevtutil qe \"Microsoft-Windows-WindowsUpdateClient/Operational\" /c:50 /f:text",
                        log, ct, true).ConfigureAwait(false);
                }

                log("---- Reset Windows Update ----");

                await StopServiceSafeAsync("wuauserv", log, ct).ConfigureAwait(false);
                await StopServiceSafeAsync("bits", log, ct).ConfigureAwait(false);
                await StopServiceSafeAsync("cryptsvc", log, ct).ConfigureAwait(false);
                await StopServiceSafeAsync("msiserver", log, ct).ConfigureAwait(false);

                // Limpiar cola BITS
                await RunAndLogAsync(
                    "del /q /f \"%ALLUSERSPROFILE%\\Application Data\\Microsoft\\Network\\Downloader\\qmgr*.dat\" 2>nul",
                    log, ct, false).ConfigureAwait(false);

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                await RenameDirSafeAsync(@"C:\Windows\SoftwareDistribution", "SoftwareDistribution_bak_" + stamp, log, ct).ConfigureAwait(false);
                await RenameDirSafeAsync(@"C:\Windows\System32\catroot2", "catroot2_bak_" + stamp, log, ct).ConfigureAwait(false);

                await StartServiceSafeAsync("cryptsvc", log, ct).ConfigureAwait(false);
                await StartServiceSafeAsync("bits", log, ct).ConfigureAwait(false);
                await StartServiceSafeAsync("wuauserv", log, ct).ConfigureAwait(false);
                await StartServiceSafeAsync("msiserver", log, ct).ConfigureAwait(false);

                result.ServicesSnapshotAfter = await GetServicesSnapshotAsync(log, ct).ConfigureAwait(false);

                result.Outcome = WinUpdateOutcome.Ok;
                result.ExitCode = 0;
                result.HumanSummary = "Reparación completada correctamente.";
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Outcome = WinUpdateOutcome.Fail_Canceled;
                result.ExitCode = -2;
                result.HumanSummary = "Operación cancelada.";
                return result;
            }
            catch (Exception ex)
            {
                result.Outcome = WinUpdateOutcome.Fail_Exception;
                result.ExitCode = -3;
                result.HumanSummary = "Error inesperado: " + ex.Message;
                return result;
            }
            finally
            {
                sw.Stop();
                result.Duration = sw.Elapsed;
                try { log("WinUpdate: duración total " + sw.Elapsed); } catch { }
            }
        }

        // =========================
        // Servicios
        // =========================

        private static async Task<string> GetServicesSnapshotAsync(Action<string> log, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Servicios:");

            sb.AppendLine(await QueryServiceLineAsync("wuauserv", ct).ConfigureAwait(false));
            sb.AppendLine(await QueryServiceLineAsync("bits", ct).ConfigureAwait(false));
            sb.AppendLine(await QueryServiceLineAsync("cryptsvc", ct).ConfigureAwait(false));
            sb.AppendLine(await QueryServiceLineAsync("msiserver", ct).ConfigureAwait(false));

            string snap = sb.ToString();
            log(snap.TrimEnd());
            return snap;
        }

        private static async Task<string> QueryServiceLineAsync(string service, CancellationToken ct)
        {
            var r = await RunCmdAsync("sc query " + service, ct).ConfigureAwait(false);
            return "- " + service + ": " + ExtractScState(r.Output);
        }

        private static string ExtractScState(string output)
        {
            if (string.IsNullOrEmpty(output)) return "(desconocido)";

            foreach (var line in output.Split('\n'))
            {
                if (line.IndexOf("STATE", StringComparison.OrdinalIgnoreCase) >= 0)
                    return line.Trim();
            }
            return "(desconocido)";
        }

        private static async Task StopServiceSafeAsync(string name, Action<string> log, CancellationToken ct)
        {
            var r = await RunCmdAsync("net stop " + name, ct).ConfigureAwait(false);
            LogCommandOutput("net stop " + name, r, log, true);
        }

        private static async Task StartServiceSafeAsync(string name, Action<string> log, CancellationToken ct)
        {
            var r = await RunCmdAsync("net start " + name, ct).ConfigureAwait(false);
            LogCommandOutput("net start " + name, r, log, true);
        }

        private static async Task RenameDirSafeAsync(string path, string newName, Action<string> log, CancellationToken ct)
        {
            // check existencia
            var check = await RunCmdAsync("if exist \"" + path + "\" (echo OK) else (echo NO)", ct).ConfigureAwait(false);
            if (check.Output == null || check.Output.IndexOf("OK", StringComparison.OrdinalIgnoreCase) < 0)
            {
                log("Skip ren: no existe " + path);
                return;
            }

            var r = await RunCmdAsync("ren \"" + path + "\" \"" + newName + "\"", ct).ConfigureAwait(false);
            LogCommandOutput("ren " + path, r, log, false);
        }

        // =========================
        // Logging
        // =========================

        private static async Task RunAndLogAsync(string command, Action<string> log, CancellationToken ct, bool trim)
        {
            var r = await RunCmdAsync(command, ct).ConfigureAwait(false);
            LogCommandOutput(command, r, log, trim);
        }

        private static void LogCommandOutput(string cmd, CmdResult r, Action<string> log, bool trim)
        {
            log("---- " + cmd);
            log("ExitCode: " + r.ExitCode);

            string outp = Normalize(r.Output);
            string err = Normalize(r.Error);

            if (trim) outp = TrimLines(outp, 120);

            if (!string.IsNullOrWhiteSpace(outp)) log(outp.TrimEnd());
            if (!string.IsNullOrWhiteSpace(err)) log(err.TrimEnd());

            log("-------------------------");
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\r\r\n", "\r\n");
        }

        private static string TrimLines(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var lines = text.Split('\n');
            if (lines.Length <= max) return text;

            var sb = new StringBuilder();
            for (int i = 0; i < max; i++)
                sb.AppendLine(lines[i]);

            sb.AppendLine("... (salida recortada)");
            return sb.ToString();
        }

        // =========================
        // CMD Runner (bytes + decode)
        // =========================

        private sealed class CmdResult
        {
            public int ExitCode;
            public string Output;
            public string Error;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        private static async Task<CmdResult> RunCmdAsync(string command, CancellationToken ct)
        {
            Process p = null;
            CancellationTokenRegistration reg = default(CancellationTokenRegistration);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/u /c " + command,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;

                // Cancelación: mata cmd.exe
                reg = ct.Register(() =>
                {
                    try
                    {
                        if (p != null && !p.HasExited)
                            p.Kill();
                    }
                    catch { }
                });

                p.Start();

                var outTask = ReadAllBytesAsync(p.StandardOutput.BaseStream, ct);
                var errTask = ReadAllBytesAsync(p.StandardError.BaseStream, ct);

                await WaitForExitAsync(p, ct).ConfigureAwait(false);

                byte[] outBytes = await outTask.ConfigureAwait(false);
                byte[] errBytes = await errTask.ConfigureAwait(false);

                return new CmdResult
                {
                    ExitCode = SafeExitCode(p),
                    Output = Decode(outBytes),
                    Error = Decode(errBytes)
                };
            }
            finally
            {
                try { reg.Dispose(); } catch { }
                if (p != null)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        private static int SafeExitCode(Process p)
        {
            try { return p.ExitCode; }
            catch { return -999; }
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream s, CancellationToken ct)
        {
            MemoryStream ms = null;
            try
            {
                ms = new MemoryStream();
                await s.CopyToAsync(ms, 81920, ct).ConfigureAwait(false);
                return ms.ToArray();
            }
            finally
            {
                if (ms != null)
                {
                    try { ms.Dispose(); } catch { }
                }
            }
        }

        private static Task WaitForExitAsync(Process p, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler h = null;
            CancellationTokenRegistration reg = default(CancellationTokenRegistration);

            h = (s, e) =>
            {
                try { p.Exited -= h; } catch { }
                try { reg.Dispose(); } catch { }
                tcs.TrySetResult(null);
            };

            try { p.Exited += h; } catch { }

            if (p.HasExited)
            {
                try { p.Exited -= h; } catch { }
                try { reg.Dispose(); } catch { }
                tcs.TrySetResult(null);
                return tcs.Task;
            }

            reg = ct.Register(() =>
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill();
                }
                catch { }

                try { p.Exited -= h; } catch { }
                try { reg.Dispose(); } catch { }
                tcs.TrySetCanceled();
            });

            return tcs.Task;
        }

        private static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";

            // BOM/Unicode heuristic
            if (bytes.Length > 1 && bytes[1] == 0x00)
                return Encoding.Unicode.GetString(bytes);

            try
            {
                return Encoding.GetEncoding((int)GetOEMCP()).GetString(bytes);
            }
            catch
            {
                try { return Encoding.Default.GetString(bytes); }
                catch { return ""; }
            }
        }
    }
}
