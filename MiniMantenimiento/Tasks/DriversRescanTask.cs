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
            Fail_Canceled,
            Fail_CommandError,
            Fail_Exception
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
                    StdOut = "",
                    StdErr = "",
                    HumanSummary = "El re-escaneo de drivers requiere permisos de Administrador."
                };
            }

            log("Drivers: iniciando re-escaneo de hardware (pnputil /scan-devices)...");
            log("---- PnPUtil scan-devices ----");

            // Sysnative: solo aplica si tu proceso es x86 en Windows x64.
            string pnputilSysnative = @"C:\Windows\Sysnative\pnputil.exe";
            string pnputilSystem32 = @"C:\Windows\System32\pnputil.exe";

            string exeToUse = System.IO.File.Exists(pnputilSysnative) ? pnputilSysnative : pnputilSystem32;

            // Ejecutamos vía CMD y forzamos UTF-8 para evitar “garabatos”
            string cmdArgs = "/c chcp 65001 >nul & \"" + exeToUse + "\" /scan-devices";

            log("Ejecutando: cmd.exe " + cmdArgs);

            try
            {
                ProcResult pr = await RunCmdUtf8CleanAsync(cmdArgs, log, ct).ConfigureAwait(false);
                sw.Stop();

                log("ExitCode: " + pr.exitCode);

                if (!string.IsNullOrWhiteSpace(pr.stdout))
                    log(pr.stdout.TrimEnd());

                if (!string.IsNullOrWhiteSpace(pr.stderr))
                    log(pr.stderr.TrimEnd());

                log("----------------------------");

                Outcome outcome;
                string summary;

                if (ct.IsCancellationRequested)
                {
                    outcome = Outcome.Fail_Canceled;
                    summary = "Re-escaneo cancelado.";
                }
                else if (pr.exitCode == 0)
                {
                    outcome = Outcome.Ok;
                    summary = "Re-escaneo completado.";
                }
                else
                {
                    outcome = Outcome.Fail_CommandError;
                    summary = "Re-escaneo terminó con error. Revisa el log.";
                }

                log("Drivers: " + summary);
                log("Drivers: duración total: " + sw.Elapsed);

                return new TaskResult
                {
                    ResultOutcome = outcome,
                    ExitCode = pr.exitCode,
                    Duration = sw.Elapsed,
                    StdOut = pr.stdout,
                    StdErr = pr.stderr,
                    HumanSummary = summary
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                log("Drivers: cancelado por el usuario.");

                return new TaskResult
                {
                    ResultOutcome = Outcome.Fail_Canceled,
                    ExitCode = -2,
                    Duration = sw.Elapsed,
                    StdOut = "",
                    StdErr = "",
                    HumanSummary = "Re-escaneo cancelado."
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                log("Drivers: excepción: " + ex.Message);

                return new TaskResult
                {
                    ResultOutcome = Outcome.Fail_Exception,
                    ExitCode = -3,
                    Duration = sw.Elapsed,
                    StdOut = "",
                    StdErr = ex.ToString(),
                    HumanSummary = "Re-escaneo falló por excepción. Revisa el log."
                };
            }
        }

        private struct ProcResult
        {
            public int exitCode;
            public string stdout;
            public string stderr;
        }

        private static Task<ProcResult> RunCmdUtf8CleanAsync(string cmdArgs, Action<string> log, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ProcResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            Process p = null;
            CancellationTokenRegistration ctr = default(CancellationTokenRegistration);

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

            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();

            bool lastOutWasBlank = false;
            bool lastErrWasBlank = false;

            try
            {
                p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;

                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;

                    string line = e.Data;
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

                    // Si quieres log “en vivo” (útil para feedback)
                    // try { log(line); } catch { }
                };

                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;

                    string line = e.Data;
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

                    // try { log(line); } catch { }
                };

                p.Exited += (s, e) =>
                {
                    try
                    {
                        var r = new ProcResult
                        {
                            exitCode = SafeGetExitCode(p),
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
                        try { ctr.Dispose(); } catch { }
                        try { if (p != null) p.Dispose(); } catch { }
                    }
                };

                ctr = ct.Register(() =>
                {
                    try
                    {
                        if (p != null && !p.HasExited)
                            TryKill(p);
                    }
                    catch { }

                    tcs.TrySetCanceled();
                });

                ct.ThrowIfCancellationRequested();

                p.Start();

                // IMPORTANTÍSIMO: sin esto NO llegan datos a OutputDataReceived/ErrorDataReceived
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                try { if (p != null && !p.HasExited) TryKill(p); } catch { }
                try { ctr.Dispose(); } catch { }
                try { if (p != null) p.Dispose(); } catch { }
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        private static int SafeGetExitCode(Process p)
        {
            try { return p.ExitCode; }
            catch { return -999; }
        }

        private static void TryKill(Process p)
        {
            try
            {
                // .NET Framework: no hay "entireProcessTree"
                p.Kill();
            }
            catch { }
        }

        private static bool ShouldSkipLine(string line)
        {
            if (line == null) return true;

            // PnPUtil normalmente es limpio, pero Windows a veces saca “artefactos”
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
                if (!char.IsLetterOrDigit(c) &&
                    c != '.' && c != ',' && c != ':' && c != '/' && c != '-' && c != '%' &&
                    c != '(' && c != ')' && c != '[' && c != ']')
                    weird++;
            }
            return (total >= 20 && weird > (total * 0.6));
        }
    }
}
