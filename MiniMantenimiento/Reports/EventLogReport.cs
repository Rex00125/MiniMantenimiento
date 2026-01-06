using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MiniMantenimiento.Reports
{
    public static class EventLogReport
    {
        // Niveles Event Log (Windows)
        // 1=Critical, 2=Error, 3=Warning, 4=Information, 5=Verbose
        public enum Level
        {
            Critical = 1,
            Error = 2,
            Warning = 3,
            Information = 4,
            Verbose = 5
        }

        public class Options
        {
            public List<string> LogNames = new List<string>();     // "System", "Application", etc.
            public List<Level> Levels = new List<Level>();         // Levels seleccionados
            public DateTime FromUtc;                               // Inicio del rango (UTC)
            public int MaxEvents = 1000;                           // Límite total
            public bool IncludeFullMessage = false;                // mensaje completo
        }

        public class Entry
        {
            public DateTime? TimeCreatedLocal;
            public string LogName;
            public Level? Level;
            public int? EventId;
            public string Provider;
            public string MachineName;
            public string User;            // DOMAIN\User o SID (fallback)
            public string TaskCategory;
            public string Message;         // puede ser null si no se pudo formatear
        }

        public class Summary
        {
            public int Critical;
            public int Error;
            public int Warning;
            public int Information;
            public int Verbose;

            public int Total => Critical + Error + Warning + Information + Verbose;
        }

        public class GroupedEntry
        {
            public string Key;                 // firma del grupo
            public string LogName;
            public Level? Level;
            public int? EventId;
            public string Provider;

            public int Count;
            public DateTime? FirstLocal;
            public DateTime? LastLocal;

            public bool Actionable;
            public string ActionableReason;    // breve (por qué)
            public string SampleMessage;       // muestra corta
        }

        public class TemporalInsight
        {
            public string Title;
            public string Detail;
        }

        public class Result
        {
            public List<Entry> Entries = new List<Entry>();
            public List<string> Warnings = new List<string>(); // warnings no críticos (ej. permisos)
        }

        // ==========================
        // HELPERS
        // ==========================
        public static Summary BuildSummary(IEnumerable<Entry> entries)
        {
            var s = new Summary();
            foreach (var e in entries)
            {
                if (!e.Level.HasValue) continue;
                switch (e.Level.Value)
                {
                    case Level.Critical: s.Critical++; break;
                    case Level.Error: s.Error++; break;
                    case Level.Warning: s.Warning++; break;
                    case Level.Information: s.Information++; break;
                    case Level.Verbose: s.Verbose++; break;
                }
            }
            return s;
        }

        private static string NormalizeForKey(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return "";
            msg = msg.Replace("\r\n", " ").Replace("\n", " ").Trim();

            // ✅ antes 180, ahora 350 para no perder códigos/errores al final
            if (msg.Length > 350) msg = msg.Substring(0, 350);

            return msg;
        }

        public static List<GroupedEntry> GroupDuplicates(IEnumerable<Entry> entries)
        {
            // Agrupa por: Log + Level + Provider + EventId + firma del mensaje
            var groups = entries
                .GroupBy(e => new
                {
                    Log = e.LogName ?? "",
                    Lvl = e.Level.HasValue ? (int)e.Level.Value : 0,
                    Prov = e.Provider ?? "",
                    Id = e.EventId.HasValue ? e.EventId.Value : 0,
                    Msg = NormalizeForKey(e.Message)
                })
                .Select(g =>
                {
                    var first = g.Where(x => x.TimeCreatedLocal.HasValue)
                                 .OrderBy(x => x.TimeCreatedLocal.Value)
                                 .FirstOrDefault();

                    var last = g.Where(x => x.TimeCreatedLocal.HasValue)
                                .OrderByDescending(x => x.TimeCreatedLocal.Value)
                                .FirstOrDefault();

                    var sample = g.Select(x => x.Message)
                                  .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                    sample = NormalizeForKey(sample);

                    var ge = new GroupedEntry
                    {
                        LogName = g.Key.Log,
                        Level = g.Key.Lvl == 0 ? (Level?)null : (Level)g.Key.Lvl,
                        Provider = g.Key.Prov,
                        EventId = g.Key.Id == 0 ? (int?)null : g.Key.Id,
                        Count = g.Count(),
                        FirstLocal = first?.TimeCreatedLocal,
                        LastLocal = last?.TimeCreatedLocal,
                        SampleMessage = sample
                    };

                    ge.Key = $"{ge.LogName}|{ge.Level}|{ge.Provider}|{ge.EventId}|{g.Key.Msg}";
                    ApplyActionableHeuristic(ge);
                    return ge;
                })
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.Level.HasValue ? (int)x.Level.Value : 999)
                .ThenBy(x => x.Provider)
                .ToList();

            return groups;
        }

        private static void ApplyActionableHeuristic(GroupedEntry g)
        {
            // Heurísticas prácticas para triage (no perfectas)
            string prov = (g.Provider ?? "").ToLowerInvariant();
            int id = g.EventId ?? -1;

            // NO actionable comunes
            if (prov.Contains("distributedcom") && (id == 10016 || id == 10010))
            {
                g.Actionable = false;
                g.ActionableReason = "DCOM típico (ruido frecuente).";
                return;
            }

            // Actionable fuertes (almacenamiento / corrupción / hardware)
            if (prov.Contains("disk") || prov.Contains("ntfs") || prov.Contains("storahci") || prov.Contains("stornvme"))
            {
                g.Actionable = true;
                g.ActionableReason = "Almacenamiento/Disco (revisar salud SMART/cables/errores).";
                return;
            }

            if (prov.Contains("whea") || prov.Contains("whealogger"))
            {
                g.Actionable = true;
                g.ActionableReason = "WHEA (hardware/CPU/RAM/PCIe).";
                return;
            }

            if (prov.Contains("volsnap") || prov.Contains("vss"))
            {
                g.Actionable = true;
                g.ActionableReason = "VSS/VolSnap (copias sombra/backups).";
                return;
            }

            if (prov.Contains("windowsupdateclient") || prov.Contains("wu") || prov.Contains("usoclient"))
            {
                g.Actionable = true;
                g.ActionableReason = "Windows Update (posible atasco/fallo de actualización).";
                return;
            }

            if (prov.Contains("service control manager") || prov.Contains("servicecontrolmanager"))
            {
                g.Actionable = true;
                g.ActionableReason = "Servicios fallando (puede impactar arranque/rendimiento).";
                return;
            }

            // Kernel-Power 41 / BugCheck / reboot inesperado
            if (prov.Contains("kernel-power") && id == 41)
            {
                g.Actionable = true;
                g.ActionableReason = "Apagado inesperado (energía, crash, PSU, temperatura).";
                return;
            }

            if (prov.Contains("bugcheck") || id == 1001)
            {
                g.Actionable = true;
                g.ActionableReason = "Pantallazo/Crash (revisar minidumps).";
                return;
            }

            // Default: si es Critical/Error y se repite mucho, es “posiblemente actionable”
            if (g.Level.HasValue && (g.Level.Value == Level.Critical || g.Level.Value == Level.Error) && g.Count >= 5)
            {
                g.Actionable = true;
                g.ActionableReason = "Error recurrente (priorizar revisión).";
                return;
            }

            g.Actionable = false;
            g.ActionableReason = null;
        }

        public static List<TemporalInsight> BuildTemporalInsights(IEnumerable<Entry> entries)
        {
            var list = new List<TemporalInsight>();

            // 1) Detectar reinicios/apagados cercanos usando IDs típicos
            // 6005 (Event Log started), 6006 (stopped), 6008 (unexpected shutdown), 1074 (planned shutdown), Kernel-Power 41
            // ✅ FIX: corregida precedencia de &&/||
            var rebootMarkers = entries.Where(e =>
                (
                    (
                        (e.Provider ?? "").IndexOf("EventLog", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (e.EventId == 6005 || e.EventId == 6006 || e.EventId == 6008)
                    )
                    ||
                    (
                        (e.Provider ?? "").IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        e.EventId == 41
                    )
                    ||
                    (e.EventId == 1074)
                )
            )
            .Where(e => e.TimeCreatedLocal.HasValue)
            .OrderByDescending(e => e.TimeCreatedLocal.Value)
            .Take(5)
            .ToList();

            if (rebootMarkers.Count > 0)
            {
                var last = rebootMarkers.First();
                list.Add(new TemporalInsight
                {
                    Title = "📍 Contexto: reinicios/apagados detectados",
                    Detail = "Se detectaron marcadores recientes (ej. 6005/6006/6008/1074/Kernel-Power 41). Último marcador: " +
                             last.TimeCreatedLocal.Value.ToString("yyyy-MM-dd HH:mm:ss") +
                             " (" + (last.Provider ?? "N/A") + " / " + (last.EventId.HasValue ? last.EventId.Value.ToString() : "N/A") + ")"
                });
            }

            // 2) Pico de errores: ventana de 10 minutos con más (Critical+Error)
            var errs = entries
                .Where(e => e.TimeCreatedLocal.HasValue && e.Level.HasValue && (e.Level.Value == Level.Critical || e.Level.Value == Level.Error))
                .OrderBy(e => e.TimeCreatedLocal.Value)
                .Select(e => e.TimeCreatedLocal.Value)
                .ToList();

            if (errs.Count >= 10)
            {
                int bestCount = 0;
                DateTime bestStart = errs[0];
                int j = 0;

                for (int i = 0; i < errs.Count; i++)
                {
                    while (j < errs.Count && (errs[j] - errs[i]).TotalMinutes <= 10) j++;
                    int count = j - i;
                    if (count > bestCount)
                    {
                        bestCount = count;
                        bestStart = errs[i];
                    }
                }

                if (bestCount >= 10)
                {
                    list.Add(new TemporalInsight
                    {
                        Title = "📍 Pico de errores (ventana 10 min)",
                        Detail = $"Se observó un pico de {bestCount} errores/críticos entre {bestStart:yyyy-MM-dd HH:mm:ss} y {bestStart.AddMinutes(10):yyyy-MM-dd HH:mm:ss}."
                    });
                }
            }

            return list;
        }

        // ==========================
        // GENERATE
        // ==========================
        public static Result Generate(Options opt, CancellationToken ct = default(CancellationToken), Action<int> progress = null)
        {
            if (opt == null) throw new ArgumentNullException(nameof(opt));
            if (opt.LogNames == null || opt.LogNames.Count == 0) throw new ArgumentException("Debe seleccionar al menos un log.");
            if (opt.Levels == null || opt.Levels.Count == 0) throw new ArgumentException("Debe seleccionar al menos un nivel.");
            if (opt.MaxEvents <= 0) opt.MaxEvents = 1000;

            var res = new Result();

            // Convertimos FromUtc a string ISO para XPath (UTC)
            string fromIsoUtc = opt.FromUtc.ToString("o", CultureInfo.InvariantCulture);

            // Filtro: TimeCreated >= from + Level in (...)
            string levelFilter = string.Join(" or ", opt.Levels.Select(l => "Level=" + ((int)l).ToString(CultureInfo.InvariantCulture)));
            string query = "*[System[TimeCreated[@SystemTime>='" + fromIsoUtc + "'] and (" + levelFilter + ")]]";

            int total = 0;

            foreach (string logName in opt.LogNames)
            {
                ct.ThrowIfCancellationRequested();
                if (total >= opt.MaxEvents) break;

                try
                {
                    var elq = new EventLogQuery(logName, PathType.LogName, query)
                    {
                        ReverseDirection = true, // más recientes primero
                        TolerateQueryErrors = true
                    };

                    using (var reader = new EventLogReader(elq))
                    {
                        for (; ; )
                        {
                            ct.ThrowIfCancellationRequested();
                            if (total >= opt.MaxEvents) break;

                            EventRecord ev = null;
                            try
                            {
                                ev = reader.ReadEvent();
                            }
                            catch (EventLogException ex)
                            {
                                res.Warnings.Add("[" + logName + "] Advertencia al leer eventos: " + ex.Message);
                                break;
                            }

                            if (ev == null) break;

                            using (ev)
                            {
                                var entry = new Entry
                                {
                                    LogName = logName,
                                    TimeCreatedLocal = ev.TimeCreated.HasValue ? ev.TimeCreated.Value.ToLocalTime() : (DateTime?)null,
                                    EventId = ev.Id,
                                    Provider = ev.ProviderName,
                                    MachineName = ev.MachineName,
                                    TaskCategory = ev.TaskDisplayName,
                                    User = TryGetUser(ev),
                                    Level = MapLevel(ev.Level)
                                };

                                entry.Message = opt.IncludeFullMessage ? TryFormatMessage(ev) : TryFormatMessageShort(ev);

                                res.Entries.Add(entry);
                                total++;

                                if (progress != null && (total % 50 == 0))
                                    progress(total);
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    res.Warnings.Add("[" + logName + "] Acceso denegado (prueba como administrador o evita este log).");
                }
                catch (Exception ex)
                {
                    res.Warnings.Add("[" + logName + "] Error al consultar: " + ex.Message);
                }
            }

            progress?.Invoke(total);
            return res;
        }

        // ✅ ahora intenta traducir a DOMAIN\Usuario (fallback a SID)
        private static string TryGetUser(EventRecord ev)
        {
            try
            {
                if (ev.UserId == null) return null;

                try
                {
                    var nt = (System.Security.Principal.NTAccount)ev.UserId.Translate(typeof(System.Security.Principal.NTAccount));
                    return nt.Value;
                }
                catch
                {
                    return ev.UserId.Value; // SID fallback
                }
            }
            catch { return null; }
        }

        private static Level? MapLevel(byte? level)
        {
            if (!level.HasValue) return null;
            switch (level.Value)
            {
                case 1: return Level.Critical;
                case 2: return Level.Error;
                case 3: return Level.Warning;
                case 4: return Level.Information;
                case 5: return Level.Verbose;
                default: return null;
            }
        }

        private static string TryFormatMessage(EventRecord ev)
        {
            try
            {
                string msg = ev.FormatDescription();
                return string.IsNullOrWhiteSpace(msg) ? null : msg.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string TryFormatMessageShort(EventRecord ev)
        {
            string full = TryFormatMessage(ev);
            if (string.IsNullOrWhiteSpace(full)) return null;

            full = full.Replace("\r\n", " ").Replace("\n", " ").Trim();
            if (full.Length <= 220) return full;
            return full.Substring(0, 220) + "...";
        }

        // ==========================
        // EXPORT
        // ==========================
        public static void ExportTxt(Result result, string path)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var sb = new StringBuilder();
            var summary = BuildSummary(result.Entries);
            var grouped = GroupDuplicates(result.Entries);
            var actionable = grouped.Where(g => g.Actionable).OrderByDescending(g => g.Count).Take(25).ToList();
            var insights = BuildTemporalInsights(result.Entries);

            sb.AppendLine("=== Event Log Report (Ejecutivo/Técnico) ===");
            sb.AppendLine("Generado: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            sb.AppendLine("== Resumen por severidad ==");
            sb.AppendLine($"Errores: {summary.Error} | Críticos: {summary.Critical} | Advertencias: {summary.Warning} | Información: {summary.Information} | Verbose: {summary.Verbose}");
            sb.AppendLine($"Total exportado: {summary.Total}");
            sb.AppendLine();

            if (insights.Count > 0)
            {
                sb.AppendLine("== Contexto temporal ==");
                foreach (var i in insights)
                {
                    sb.AppendLine(i.Title);
                    sb.AppendLine("  " + i.Detail);
                }
                sb.AppendLine();
            }

            sb.AppendLine("== Eventos marcados como [ACTIONABLE] ==");
            if (actionable.Count == 0)
            {
                sb.AppendLine("(Ninguno marcado por heurística. Ojo: esto no significa que no haya problemas.)");
            }
            else
            {
                foreach (var g in actionable)
                {
                    sb.AppendLine($"[ACTIONABLE] {g.Provider} EventId {g.EventId} ({g.LogName}) – {g.Count} ocurrencias");
                    sb.AppendLine($"  Ventana: {(g.FirstLocal.HasValue ? g.FirstLocal.Value.ToString("HH:mm:ss") : "N/A")} → {(g.LastLocal.HasValue ? g.LastLocal.Value.ToString("HH:mm:ss") : "N/A")}");
                    if (!string.IsNullOrWhiteSpace(g.ActionableReason)) sb.AppendLine("  Motivo: " + g.ActionableReason);
                    if (!string.IsNullOrWhiteSpace(g.SampleMessage)) sb.AppendLine("  Ejemplo: " + g.SampleMessage);
                    sb.AppendLine();
                }
            }
            sb.AppendLine();

            sb.AppendLine("== Agrupación de eventos repetidos (Top 80 por frecuencia) ==");
            foreach (var g in grouped.Take(80))
            {
                sb.AppendLine($"{g.Provider} EventId {g.EventId} [{g.LogName}] [{(g.Level.HasValue ? g.Level.Value.ToString() : "N/A")}] – {g.Count} ocurrencias"
                    + (g.Actionable ? "  [ACTIONABLE]" : ""));
                sb.AppendLine($"  Primer: {(g.FirstLocal.HasValue ? g.FirstLocal.Value.ToString("yyyy-MM-dd HH:mm:ss") : "N/A")} | Último: {(g.LastLocal.HasValue ? g.LastLocal.Value.ToString("yyyy-MM-dd HH:mm:ss") : "N/A")}");
                if (!string.IsNullOrWhiteSpace(g.SampleMessage)) sb.AppendLine("  Muestra: " + g.SampleMessage);
                sb.AppendLine(new string('-', 80));
            }
            sb.AppendLine();

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("== Advertencias del generador ==");
                foreach (var w in result.Warnings) sb.AppendLine("- " + w);
                sb.AppendLine();
            }

            sb.AppendLine("== Conclusión honesta ==");
            sb.AppendLine("El reporte agrupa y prioriza, pero usa heurísticas: sirve para triage, no para veredicto absoluto.");
            sb.AppendLine("Si algo se repite mucho, es crítico o está marcado como [ACTIONABLE], vale la pena investigarlo primero.");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        public static void ExportCsv(Result result, string path)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var grouped = GroupDuplicates(result.Entries);

            var sb = new StringBuilder();
            sb.AppendLine("Log,Level,Provider,EventId,Count,FirstLocal,LastLocal,Actionable,Reason,SampleMessage");

            foreach (var g in grouped)
            {
                sb.AppendLine(string.Join(",",
                    Csv(g.LogName),
                    Csv(g.Level.HasValue ? g.Level.Value.ToString() : ""),
                    Csv(g.Provider),
                    Csv(g.EventId.HasValue ? g.EventId.Value.ToString() : ""),
                    Csv(g.Count.ToString()),
                    Csv(g.FirstLocal.HasValue ? g.FirstLocal.Value.ToString("yyyy-MM-dd HH:mm:ss") : ""),
                    Csv(g.LastLocal.HasValue ? g.LastLocal.Value.ToString("yyyy-MM-dd HH:mm:ss") : ""),
                    Csv(g.Actionable ? "YES" : "NO"),
                    Csv(g.ActionableReason),
                    Csv(g.SampleMessage)
                ));
            }

            // BOM para Excel
            var utf8Bom = new UTF8Encoding(true);
            File.WriteAllText(path, sb.ToString(), utf8Bom);
        }

        private static string Csv(string s)
        {
            if (s == null) return "\"\"";
            s = s.Replace("\r\n", " ").Replace("\n", " ");
            s = s.Replace("\"", "\"\"");
            return "\"" + s + "\"";
        }
    }
}
