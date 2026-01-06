using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniMantenimiento.Tasks
{
    public static class DismTask
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
                    HumanSummary = "DISM requiere permisos de Administrador."
                };
            }

            log("DISM: reparando imagen del sistema (RestoreHealth). Esto puede tardar bastante...");
            log("---- DISM RestoreHealth ----");

            var dismCmd = "dism /Online /Cleanup-Image /RestoreHealth";
            var cmdArgs = "/c chcp 65001 >nul & " + dismCmd;

            log("Ejecutando: cmd.exe " + cmdArgs);

            Timer heartbeat = null;
            try
            {
                heartbeat = new Timer(_ =>
                {
                    try { log("DISM: sigo trabajando... (esto es normal)"); } catch { }
                }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

                ProcResult pr = await RunCmdUtf8CleanAsync(cmdArgs, log, ct).ConfigureAwait(false);

                sw.Stop();

                Outcome outcome;
                string summary;

                if (ct.IsCancellationRequested)
                {
                    outcome = Outcome.Fail_Canceled;
                    summary = "DISM fue cancelado.";
                }
                else if (pr.exitCode == 0)
                {
                    outcome = Outcome.Ok;
                    summary = "DISM finalizó correctamente.";
                }
                else
                {
                    outcome = Outcome.Fail_CommandError;
                    summary = "DISM terminó con error. Revisa el log para el detalle.";
                }

                log("ExitCode: " + pr.exitCode);

                if (!string.IsNullOrWhiteSpace(pr.stdout))
                    log(pr.stdout.TrimEnd());

                if (!string.IsNullOrWhiteSpace(pr.stderr))
                    log(pr.stderr.TrimEnd());

                log("----------------------------");
                log("DISM: " + summary);
                log("DISM: duración total: " + sw.Elapsed);

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
                log("DISM: cancelado por el usuario.");

                return new TaskResult
                {
                    ResultOutcome = Outcome.Fail_Canceled,
                    ExitCode = -2,
                    Duration = sw.Elapsed,
                    StdOut = "",
                    StdErr = "",
                    HumanSummary = "DISM fue cancelado."
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                log("DISM: excepción: " + ex.Message);

                return new TaskResult
                {
                    ResultOutcome = Outcome.Fail_Exception,
                    ExitCode = -3,
                    Duration = sw.Elapsed,
                    StdOut = "",
                    StdErr = ex.ToString(),
                    HumanSummary = "DISM falló por excepción. Revisa el log."
                };
            }
            finally
            {
                if (heartbeat != null)
                {
                    try { heartbeat.Dispose(); } catch { }
                }
            }
        }

        private struct ProcResult
        {
            public int exitCode;
            public string stdout;
            public string stderr;
        }

        /// <summary>
        /// Ejecuta CMD con UTF-8 (chcp 65001) y limpia:
        /// - barras de progreso tipo: [===  5.4%  ]
        /// - spam de líneas vacías
        /// Además: manda líneas limpias al log en vivo.
        /// </summary>
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

                    try { log(line); } catch { }
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

                    try { log(line); } catch { }
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
                        // Limpieza aquí también por si el caller se olvida
                        try { ctr.Dispose(); } catch { }
                        try { if (p != null) p.Dispose(); } catch { }
                    }
                };

                // Cancelación: mata el proceso si sigue vivo
                ctr = ct.Register(() =>
                {
                    try
                    {
                        if (p != null && !p.HasExited)
                        {
                            TryKill(p);
                        }
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

            if (IsDismProgressLine(line))
                return true;

            if (IsMostlyWeirdSymbols(line))
                return true;

            return false;
        }

        private static bool IsDismProgressLine(string line)
        {
            var t = line.Trim();
            if (t.Length < 5) return false;
            if (t[0] != '[' || t[t.Length - 1] != ']') return false;
            if (t.IndexOf('%') < 0) return false;

            foreach (char c in t)
            {
                if (c == '[' || c == ']' || c == '=' || c == ' ' || c == '%' || c == '.' || char.IsDigit(c))
                    continue;
                return false;
            }
            return true;
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
                    c != '.' && c != ',' && c != ':' && c != '/' && c != '-' && c != '%' && c != '(' && c != ')')
                    weird++;
            }
            return (total >= 20 && weird > (total * 0.6));
        }
    }
}
