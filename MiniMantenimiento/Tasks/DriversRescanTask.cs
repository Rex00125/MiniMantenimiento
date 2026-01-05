using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniMantenimiento.Tasks
{
    public static class DriversRescanTask
    {
        public enum Outcome
        {
            Ok,
            Fail_NotElevated,
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
                    HumanSummary = "El re-escaneo de drivers requiere permisos de Administrador."
                };
            }

            log("Drivers: iniciando re-escaneo de hardware (pnputil /scan-devices)...");
            log("---- PnPUtil scan-devices ----");

            // Tip: Sysnative solo existe si eres proceso x86 en Windows x64.
            // Si tu app es AnyCPU/Prefer 32-bit, conviene.
            var pnputilPath = @"C:\Windows\Sysnative\pnputil.exe";
            var dismPath = @"C:\Windows\System32\pnputil.exe";

            // Si Sysnative no existe (app x64), usamos System32.
            var exeToUse = System.IO.File.Exists(pnputilPath) ? pnputilPath : dismPath;

            // Ejecutamos vía CMD y forzamos UTF-8 para evitar “garabatos”
            var cmd = $"/c chcp 65001 >nul & \"{exeToUse}\" /scan-devices";

            log("Ejecutando: cmd.exe " + cmd);

            ProcResult pr = await RunCmdUtf8CleanAsync(cmd, ct).ConfigureAwait(false);
            sw.Stop();

            log("ExitCode: " + pr.exitCode);

            if (!string.IsNullOrWhiteSpace(pr.stdout))
                log(pr.stdout.TrimEnd());

            if (!string.IsNullOrWhiteSpace(pr.stderr))
                log(pr.stderr.TrimEnd());

            log("----------------------------");

            var summary =
                pr.exitCode == 0
                    ? "Re-escaneo completado."
                    : "Re-escaneo terminó con error. Revisa el log.";

            log("Drivers: " + summary);
            log("Drivers: duración total: " + sw.Elapsed);

            return new TaskResult
            {
                ResultOutcome = (pr.exitCode == 0) ? Outcome.Ok : Outcome.Fail_Unknown,
                ExitCode = pr.exitCode,
                Duration = sw.Elapsed,
                StdOut = pr.stdout,
                StdErr = pr.stderr,
                HumanSummary = summary
            };
        }

        private struct ProcResult
        {
            public int exitCode;
            public string stdout;
            public string stderr;
        }

        private static Task<ProcResult> RunCmdUtf8CleanAsync(string cmdArgs, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ProcResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();

            bool lastOutWasBlank = false;
            bool lastErrWasBlank = false;

            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;

                var line = e.Data;

                if (ShouldSkipLine(line)) return;

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (lastOutWasBlank) return;
                    lastOutWasBlank = true;
                    sbOut.AppendLine();
                    return;
                }

                lastOutWasBlank = false;
                sbOut.AppendLine(line);
            };

            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;

                var line = e.Data;

                if (ShouldSkipLine(line)) return;

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (lastErrWasBlank) return;
                    lastErrWasBlank = true;
                    sbErr.AppendLine();
                    return;
                }

                lastErrWasBlank = false;
                sbErr.AppendLine(line);
            };

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

        private static bool ShouldSkipLine(string line)
        {
            if (line == null) return true;

            // Si alguna vez sale “basura” extrema, la filtramos.
            // (PnPUtil normalmente NO debería, pero ya vimos que Windows puede ser… creativo)
            if (IsMostlyWeirdSymbols(line))
                return true;

            return false;
        }

        private static bool IsMostlyWeirdSymbols(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;

            int weird = 0, total = 0;
            foreach (char c in line)
            {
                if (c == '\t' || c == ' ') continue;
                total++;
                if (!char.IsLetterOrDigit(c) && c != '.' && c != ',' && c != ':' && c != '/' && c != '-' && c != '%' && c != '(' && c != ')' && c != '[' && c != ']')
                    weird++;
            }
            return (total >= 20 && weird > (total * 0.6));
        }
    }
}
