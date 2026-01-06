using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MiniMantenimiento.Tasks
{
    internal sealed class RepairTasks
    {
        internal sealed class RepairResult
        {
            public bool IsAdmin { get; set; }
            public bool DeepMode { get; set; }
            public TimeSpan Duration { get; set; }
            public List<string> LogLines { get; } = new List<string>();

            public bool WinUpdateRan { get; set; }
            public int WinUpdateExitCode { get; set; }
            public string WinUpdateSummary { get; set; }

            public bool DismRan { get; set; }
            public int DismExitCode { get; set; }
            public string DismSummary { get; set; }

            public bool SfcRan { get; set; }
            public int SfcExitCode { get; set; }
            public string SfcSummary { get; set; }

            public bool DriversRescanRan { get; set; }
            public int DriversRescanExitCode { get; set; }
            public string DriversRescanSummary { get; set; }

            public string HumanSummary { get; set; }
        }

        internal async Task<RepairResult> RunAsync(
            bool isAdmin,
            Action<string> log,
            CancellationToken ct,
            bool deepMode)
        {
            var sw = Stopwatch.StartNew();

            var res = new RepairResult();
            res.IsAdmin = isAdmin;
            res.DeepMode = deepMode;

            Action<string> both = (msg) =>
            {
                try { res.LogLines.Add(msg); } catch { }
                try { if (log != null) log(msg); } catch { }
            };

            both("===== REPARACIÓN =====");
            both("Modo: " + (deepMode ? "Profundo" : "Rápido"));
            both("Admin: " + (isAdmin ? "Sí" : "No"));
            both("----------------------");

            if (!isAdmin)
            {
                both("AVISO: No eres Administrador. DISM/SFC/Drivers/WinUpdate no podrán ejecutarse.");
                // Aun así dejamos correr “lo que se pueda” (ahorita: nada de estas tareas).
            }

            // 1) Windows Update FIX
            try
            {
                ct.ThrowIfCancellationRequested();

                both("Paso 1/4: Windows Update (reparación segura)");
                var wu = await WinUpdateFixTask.RunAsync(isAdmin, both, ct, deepMode).ConfigureAwait(false);

                res.WinUpdateRan = true;
                res.WinUpdateExitCode = wu.ExitCode;
                res.WinUpdateSummary = wu.HumanSummary;

                both("Resultado Windows Update: " + (wu.HumanSummary ?? "(sin resumen)"));
                both("----------------------");
            }
            catch (OperationCanceledException)
            {
                both("Reparación cancelada por el usuario.");
                res.HumanSummary = "Cancelado.";
                res.Duration = sw.Elapsed;
                return res;
            }
            catch (Exception ex)
            {
                res.WinUpdateRan = true;
                res.WinUpdateExitCode = -3;
                res.WinUpdateSummary = "Excepción: " + ex.Message;
                both("Windows Update: excepción: " + ex.Message);
                both("----------------------");
            }

            // 2) DISM
            if (isAdmin)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    both("Paso 2/4: DISM RestoreHealth");
                    var dr = await DismTask.RunAsync(true, both, ct).ConfigureAwait(false);

                    res.DismRan = true;
                    res.DismExitCode = dr.ExitCode;
                    res.DismSummary = dr.HumanSummary;

                    both("Resultado DISM: " + (dr.HumanSummary ?? "(sin resumen)"));
                    both("----------------------");
                }
                catch (OperationCanceledException)
                {
                    both("Reparación cancelada por el usuario.");
                    res.HumanSummary = "Cancelado.";
                    res.Duration = sw.Elapsed;
                    return res;
                }
                catch (Exception ex)
                {
                    res.DismRan = true;
                    res.DismExitCode = -3;
                    res.DismSummary = "Excepción: " + ex.Message;
                    both("DISM: excepción: " + ex.Message);
                    both("----------------------");
                }
            }
            else
            {
                res.DismRan = false;
                res.DismExitCode = -1;
                res.DismSummary = "Saltado: requiere admin.";
                both("Paso 2/4: DISM saltado (requiere admin).");
                both("----------------------");
            }

            // 3) SFC
            if (isAdmin)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    both("Paso 3/4: SFC /scannow");
                    var sr = await SfcTask.RunAsync(true, both, ct).ConfigureAwait(false);

                    res.SfcRan = true;
                    res.SfcExitCode = sr.ExitCode;
                    res.SfcSummary = sr.HumanSummary;

                    both("Resultado SFC: " + (sr.HumanSummary ?? "(sin resumen)"));
                    both("----------------------");
                }
                catch (OperationCanceledException)
                {
                    both("Reparación cancelada por el usuario.");
                    res.HumanSummary = "Cancelado.";
                    res.Duration = sw.Elapsed;
                    return res;
                }
                catch (Exception ex)
                {
                    res.SfcRan = true;
                    res.SfcExitCode = -3;
                    res.SfcSummary = "Excepción: " + ex.Message;
                    both("SFC: excepción: " + ex.Message);
                    both("----------------------");
                }
            }
            else
            {
                res.SfcRan = false;
                res.SfcExitCode = -1;
                res.SfcSummary = "Saltado: requiere admin.";
                both("Paso 3/4: SFC saltado (requiere admin).");
                both("----------------------");
            }

            // 4) Drivers rescan
            if (isAdmin)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    both("Paso 4/4: Re-escaneo de hardware (PnPUtil /scan-devices)");
                    var rr = await DriversRescanTask.RunAsync(true, both, ct).ConfigureAwait(false);

                    res.DriversRescanRan = true;
                    res.DriversRescanExitCode = rr.ExitCode;
                    res.DriversRescanSummary = rr.HumanSummary;

                    both("Resultado Drivers: " + (rr.HumanSummary ?? "(sin resumen)"));
                    both("----------------------");
                }
                catch (OperationCanceledException)
                {
                    both("Reparación cancelada por el usuario.");
                    res.HumanSummary = "Cancelado.";
                    res.Duration = sw.Elapsed;
                    return res;
                }
                catch (Exception ex)
                {
                    res.DriversRescanRan = true;
                    res.DriversRescanExitCode = -3;
                    res.DriversRescanSummary = "Excepción: " + ex.Message;
                    both("Drivers: excepción: " + ex.Message);
                    both("----------------------");
                }
            }
            else
            {
                res.DriversRescanRan = false;
                res.DriversRescanExitCode = -1;
                res.DriversRescanSummary = "Saltado: requiere admin.";
                both("Paso 4/4: Drivers rescan saltado (requiere admin).");
                both("----------------------");
            }

            sw.Stop();
            res.Duration = sw.Elapsed;

            res.HumanSummary = BuildHumanSummary(res);
            both("RESUMEN: " + res.HumanSummary);
            both("Duración total: " + res.Duration);
            both("======================");

            return res;
        }

        private static string BuildHumanSummary(RepairResult r)
        {
            bool anyFail = false;

            if (r.WinUpdateRan && r.WinUpdateExitCode != 0) anyFail = true;
            if (r.DismRan && r.DismExitCode != 0) anyFail = true;
            if (r.SfcRan && r.SfcExitCode != 0) anyFail = true;
            if (r.DriversRescanRan && r.DriversRescanExitCode != 0) anyFail = true;

            if (!r.IsAdmin)
                return anyFail
                    ? "Se ejecutó parcialmente (sin admin) con errores/advertencias."
                    : "Se ejecutó parcialmente (sin admin).";

            return anyFail
                ? "Reparación completada con advertencias/errores (ver log)."
                : "Reparación completada correctamente.";
        }
    }
}
