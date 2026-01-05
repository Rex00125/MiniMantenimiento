using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MiniMantenimiento.Tasks
{
    public static class DismTask
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
                    HumanSummary = "DISM requiere permisos de Administrador."
                };
            }

            log("DISM: reparando imagen del sistema (RestoreHealth). Esto puede tardar bastante...");
            log("---- DISM RestoreHealth ----");

            var dismCmd = "dism /Online /Cleanup-Image /RestoreHealth";
            var cmdArgs = "/c chcp 65001 >nul & " + dismCmd;

            log("Ejecutando: cmd.exe " + cmdArgs);

            var heartbeat = new System.Windows.Forms.Timer();
            heartbeat.Interval = 20000;
            heartbeat.Tick += (s, e) => log("DISM: sigo trabajando... (esto es normal)");
            heartbeat.Start();

            ProcResult pr;
            try
            {
                pr = await RunCmdUtf8CleanAsync(cmdArgs, ct).ConfigureAwait(false);
            }
            finally
            {
                try { heartbeat.Stop(); } catch { }
                try { heartbeat.Dispose(); } catch { }
                sw.Stop();
            }

            var summary = (pr.exitCode == 0)
                ? "DISM finalizó correctamente."
                : "DISM terminó con error. Revisa el log para el detalle.";

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

        /// <summary>
        /// Ejecuta CMD forzando UTF-8 (chcp 65001) y limpia:
        /// - líneas de progreso tipo: [===  5.4%  ]
        /// - spam de líneas vacías
        /// </summary>
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

            // Para evitar que DISM nos meta 200 saltos de línea seguidos.
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
                    sbOut.AppendLine(); // deja solo un salto
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

            // 1) Progreso DISM típico:
            // "[==                         3.8%                           ]"
            if (IsDismProgressLine(line))
                return true;

            // 2) Por si DISM suelta “artefactos” raros:
            if (IsMostlyWeirdSymbols(line))
                return true;

            return false;
        }

        private static bool IsDismProgressLine(string line)
        {
            // Formato: empieza con [, termina con ], contiene %,
            // y casi todo son = espacios y números/punto.
            var t = line.Trim();
            if (t.Length < 5) return false;
            if (t[0] != '[' || t[t.Length - 1] != ']') return false;
            if (t.IndexOf('%') < 0) return false;

            foreach (char c in t)
            {
                if (c == '[' || c == ']' || c == '=' || c == ' ' || c == '%' || c == '.' || char.IsDigit(c))
                    continue;
                return false; // si trae letras, ya no es la barrita
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
                if (!char.IsLetterOrDigit(c) && c != '.' && c != ',' && c != ':' && c != '/' && c != '-' && c != '%' && c != '(' && c != ')')
                    weird++;
            }
            return (total >= 20 && weird > (total * 0.6));
        }
    }
}
