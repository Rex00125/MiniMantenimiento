using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniMantenimiento.Tasks
{
    public static class SfcTask
    {
        public enum Outcome
        {
            Ok_NoViolations,
            Ok_Repaired,
            Ok_RepairedRebootRequired,
            Fail_NotElevated,
            Fail_TrustedInstaller,
            Fail_Unknown
        }

        public sealed class TaskResult
        {
            public Outcome ResultOutcome { get; set; }
            public int ExitCode { get; set; }
            public TimeSpan Duration { get; set; }
            public string StdOut { get; set; }
            public string StdErr { get; set; }
            public string HumanSummary { get; set; }
        }

        public static async Task<TaskResult> RunAsync(bool isAdmin, Action<string> log, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            if (!isAdmin)
            {
                return new TaskResult
                {
                    ResultOutcome = Outcome.Fail_NotElevated,
                    ExitCode = -1,
                    Duration = TimeSpan.Zero,
                    HumanSummary = "SFC requiere permisos de Administrador."
                };
            }

            log("SFC: iniciando análisis de archivos del sistema...");

            // 1) Asegurar TrustedInstaller (Windows Modules Installer)
            var tiOk = await EnsureTrustedInstallerRunningAsync(log, ct).ConfigureAwait(false);
            if (!tiOk)
            {
                sw.Stop();
                return new TaskResult
                {
                    ResultOutcome = Outcome.Fail_TrustedInstaller,
                    ExitCode = 1,
                    Duration = sw.Elapsed,
                    HumanSummary = "No se pudo iniciar/usar TrustedInstaller (Windows Modules Installer). Ejecuta CMD como admin y valida el servicio."
                };
            }

            // 2) Ejecutar SFC
            log("---- SFC /scannow ----");
            var cmd = "sfc /scannow";
            log("Ejecutando: cmd.exe /u /c " + cmd);

            var pr = await RunCmdUnicodeAsync(cmd, ct).ConfigureAwait(false);

            sw.Stop();

            var summary = SummarizeSfc(pr.stdout + "\n" + pr.stderr, pr.exitCode);

            log("ExitCode: " + pr.exitCode);
            if (!string.IsNullOrWhiteSpace(pr.stdout)) log(pr.stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(pr.stderr)) log(pr.stderr.TrimEnd());
            log("----------------------");
            log("SFC: " + summary.human);

            return new TaskResult
            {
                ResultOutcome = summary.outcome,
                ExitCode = pr.exitCode,
                Duration = sw.Elapsed,
                StdOut = pr.stdout,
                StdErr = pr.stderr,
                HumanSummary = summary.human
            };
        }

        // ===================== Helpers =====================

        private static async Task<bool> EnsureTrustedInstallerRunningAsync(Action<string> log, CancellationToken ct)
        {
            // Query
            var q = await RunCmdUnicodeAsync("sc query trustedinstaller", ct).ConfigureAwait(false);
            var text = (q.stdout ?? "") + "\n" + (q.stderr ?? "");
            var running = text.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
            var stopped = text.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0;

            log("TrustedInstaller status inicial: " + (running ? "RUNNING" : (stopped ? "STOPPED" : "DESCONOCIDO")));

            if (running) return true;

            // Try start (sin cambiar config)
            log("Intentando iniciar TrustedInstaller...");
            var s = await RunCmdUnicodeAsync("net start trustedinstaller", ct).ConfigureAwait(false);
            var sText = (s.stdout ?? "") + "\n" + (s.stderr ?? "");

            if (sText.IndexOf("Acceso denegado", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sText.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                log("TrustedInstaller: acceso denegado al intentar iniciarlo (falta elevar permisos).");
                return false;
            }

            // Re-query
            var q2 = await RunCmdUnicodeAsync("sc query trustedinstaller", ct).ConfigureAwait(false);
            var text2 = (q2.stdout ?? "") + "\n" + (q2.stderr ?? "");
            var running2 = text2.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;

            log("TrustedInstaller status tras Start(): " + (running2 ? "RUNNING" : "NO RUNNING"));
            return running2;
        }

        private static (Outcome outcome, string human) SummarizeSfc(string all, int exitCode)
        {
            var t = all ?? "";

            // Mensajes típicos en ES
            if (t.IndexOf("no encontró ninguna infracción de integridad", StringComparison.OrdinalIgnoreCase) >= 0)
                return (Outcome.Ok_NoViolations, "No se encontró ninguna infracción de integridad.");

            if (t.IndexOf("encontró archivos dañados y los reparó correctamente", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // algunos dicen que los cambios surten efecto tras reinicio
                if (t.IndexOf("surtirá efecto en el siguiente reinicio", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (Outcome.Ok_RepairedRebootRequired, "Reparó archivos correctamente. Requiere reinicio para aplicar cambios.");
                return (Outcome.Ok_Repaired, "Reparó archivos dañados correctamente.");
            }

            if (t.IndexOf("no pudo iniciar el servicio de reparación", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("could not start the repair service", StringComparison.OrdinalIgnoreCase) >= 0)
                return (Outcome.Fail_TrustedInstaller, "Protección de recursos de Windows no pudo iniciar el servicio de reparación (TrustedInstaller).");

            // fallback
            return (exitCode == 0 ? Outcome.Ok_NoViolations : Outcome.Fail_Unknown,
                exitCode == 0 ? "SFC terminó sin errores." : "SFC terminó con error. Revisa el log para el detalle.");
        }

        private struct ProcResult
        {
            public int exitCode;
            public string stdout;
            public string stderr;
        }

        private static Task<ProcResult> RunCmdUnicodeAsync(string command, CancellationToken ct)
        {
            // cmd.exe /u => Unicode (UTF-16LE). Lo leemos con Encoding.Unicode.
            var tcs = new TaskCompletionSource<ProcResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/u /c " + command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Unicode,
                StandardErrorEncoding = Encoding.Unicode
            };

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();

            p.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };

            p.Exited += (s, e) =>
            {
                try
                {
                    var r = new ProcResult
                    {
                        exitCode = p.ExitCode,
                        stdout = sbOut.ToString(),
                        stderr = sbErr.ToString()
                    };
                    tcs.TrySetResult(r);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            };

            ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                tcs.TrySetCanceled();
            });

            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                try { p.Dispose(); } catch { }
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }
    }
}
