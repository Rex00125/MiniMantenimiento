// Form1.cs
// Requiere referencia: System.Management
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using MiniMantenimiento.Reports;
using MiniMantenimiento.Tasks;
using MiniMantenimiento.Helpers;
using MiniMantenimiento.Services;
using MiniMantenimiento.Utils;

namespace MiniMantenimiento
{
    public partial class Form1 : Form
    {
        // backing log (para exportar)
        private readonly StringBuilder _log = new StringBuilder();

        // Logger desacoplado
        private UiLogger _logger;

        // Servicios extraídos
        private readonly HealthCheckService _health = new HealthCheckService();
        private readonly CleanupService _cleanup = new CleanupService();
        private readonly CommandRunner _runner = new CommandRunner();
        private NetworkResetService _netReset;

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Logger (ya con UI lista)
            _logger = new UiLogger(this, txtLog, _log);
            _netReset = new NetworkResetService(_runner);

            // Status inicial
            SetStatus("Listo.");

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
            RefreshAdminUi();

            _logger.Log("App iniciada.");
        }

        // =========================
        // Helpers UI
        // =========================

        private void SetStatus(string text)
        {
            if (lblStatus == null) return;

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)(() => { lblStatus.Text = text ?? ""; }));
                    return;
                }
                lblStatus.Text = text ?? "";
            }
            catch { }
        }

        private void RefreshAdminUi()
        {
            bool isAdmin = AdminHelper.IsRunningAsAdmin();

            lblAdminStatus.Text = isAdmin
                ? "Estado: Ejecutando como Administrador ✅ (reparaciones avanzadas habilitadas)"
                : "Estado: Sin permisos de Administrador ⚠️ (algunas reparaciones se deshabilitarán)";

            // Botones que sí requieren admin
            btnSfc.Enabled = isAdmin;
            btnDism.Enabled = isAdmin;
            btnWinUpdateFix.Enabled = isAdmin;
            btnDriversRescan.Enabled = isAdmin;

            // NetReset: tú decidías. Lo dejo como admin porque tu flujo lo trae así.
            btnNetReset.Enabled = isAdmin;
        }

        private void SetBusy(bool busy, string statusText)
        {
            SetStatus(statusText);

            pb.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
            pb.MarqueeAnimationSpeed = busy ? 25 : 0;

            // Evita ejecutar tareas en paralelo (se pisan logs / servicios)
            gbAcciones.Enabled = !busy;
            gbEventReport.Enabled = !busy;
        }

        // =========================
        // Chequeo rápido (servicio)
        // =========================
        private void btnHealthCheck_Click(object sender, EventArgs e)
        {
            btnHealthCheck.Enabled = false;
            pb.Style = ProgressBarStyle.Marquee;
            SetStatus("Chequeo rápido en progreso...");

            _logger.Log("Chequeo rápido: iniciando...");

            Task.Run(() =>
            {
                try
                {
                    var lines = _health.BuildHealthCheckLines();

                    BeginInvoke((Action)(() =>
                    {
                        foreach (var line in lines)
                            _logger.Log(line);

                        _logger.Log("Chequeo rápido: listo ✅");
                        SetStatus("Listo.");
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)(() =>
                    {
                        _logger.Log("Chequeo rápido: error ❌ " + ex.Message);
                        SetStatus("Error en Chequeo rápido.");
                        MessageBox.Show(this, "Error en Chequeo rápido:\n\n" + ex, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    BeginInvoke((Action)(() =>
                    {
                        pb.Style = ProgressBarStyle.Blocks;
                        btnHealthCheck.Enabled = true;
                    }));
                }
            });
        }

        // =========================
        // Limpieza (temporales + papelera)
        // =========================
        private void btnCleanup_Click(object sender, EventArgs e)
        {
            btnCleanup.Enabled = false;
            pb.Style = ProgressBarStyle.Marquee;
            SetStatus("Limpieza en progreso...");

            bool emptyBin = (chkEmptyRecycleBin != null && chkEmptyRecycleBin.Checked);

            _logger.Log("Limpieza: iniciando...");
            if (emptyBin) _logger.Log("Limpieza: Papelera marcada para vaciar.");

            Task.Run(() =>
            {
                CleanupService.CleanupResult result = null;
                Exception caught = null;

                try
                {
                    result = _cleanup.RunCleanup();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (caught != null)
                        {
                            _logger.Log("Limpieza: error ❌ " + caught.Message);
                            SetStatus("Error en Limpieza.");
                            MessageBox.Show(this, "Error al limpiar temporales:\n\n" + caught, "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        foreach (var line in result.LogLines)
                            _logger.Log(line);

                        if (emptyBin)
                        {
                            _logger.Log("Vaciando Papelera...");
                            string msg;
                            int hr;
                            bool ok = _cleanup.TryEmptyRecycleBin(out msg, out hr);

                            _logger.Log(ok
                                ? "  -> Papelera vaciada ✅"
                                : ("  -> Papelera: " + (msg ?? "falló") + " (hr=0x" + hr.ToString("X8") + ")"));
                        }

                        _logger.Log($"Limpieza: liberado aprox. {result.FreedBytes} bytes ✅");
                        SetStatus("Limpieza completada.");
                    }
                    finally
                    {
                        pb.Style = ProgressBarStyle.Blocks;
                        btnCleanup.Enabled = true;
                        SetStatus("Listo.");
                    }
                }));
            });
        }

        // =========================
        // Reset de red (servicio)
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
            SetStatus("Reset de red en progreso...");

            _logger.Log("Reset de red: iniciando...");

            Task.Run(() =>
            {
                NetworkResetService.NetworkResetResult result = null;
                Exception caught = null;

                try
                {
                    result = _netReset.Run(timeoutMsPerCommand: 120000, deepMode: false);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (caught != null)
                        {
                            _logger.Log("Reset de red: error ❌ " + caught.Message);
                            SetStatus("Error en Reset de red.");
                            MessageBox.Show(this,
                                "Error durante el Reset de red:\n\n" + caught,
                                "Error",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            return;
                        }

                        _logger.LogNetworkResetResult(result);

                        if (result.AnyCritical)
                        {
                            MessageBox.Show(this,
                                "El Reset de red finalizó con algunos errores.\n\n" +
                                "Revisa el log para más detalles.\n" +
                                "Se recomienda reiniciar el equipo.",
                                "Reset de red",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                        else if (result.AnyPartial)
                        {
                            MessageBox.Show(this,
                                "Reset de red completado con advertencias.\n\n" +
                                "Reinicia el equipo para finalizar.",
                                "Reset de red",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show(this,
                                "Reset de red completado correctamente.\n\n" +
                                "Reinicia el equipo para aplicar todos los cambios.",
                                "Reset de red",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }

                        SetStatus("Reset de red finalizado.");
                    }
                    finally
                    {
                        pb.Style = ProgressBarStyle.Blocks;
                        btnNetReset.Enabled = AdminHelper.IsRunningAsAdmin();
                        SetStatus("Listo.");
                    }
                }));
            });
        }

        // =========================
        // SFC
        // =========================
        private async void btnSfc_Click(object sender, EventArgs e)
        {
            if (!AdminHelper.IsRunningAsAdmin())
            {
                MessageBox.Show(this, "SFC requiere permisos de Administrador.", "SFC",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshAdminUi();
                return;
            }

            btnSfc.Enabled = false;
            var oldStyle = pb.Style;
            pb.Style = ProgressBarStyle.Marquee;
            SetStatus("SFC ejecutándose...");

            var cts = new CancellationTokenSource();

            try
            {
                _logger.Log("SFC: iniciando...");
                var result = await SfcTask.RunAsync(true, _logger.Log, cts.Token).ConfigureAwait(true);

                _logger.Log("SFC: duración total: " + result.Duration);

                var icon = MessageBoxIcon.Information;

                // Si tu SfcTask es el “viejo” enum: estas condiciones siguen funcionando.
                // Si es el “nuevo”, también aplica (Fail_*).
                if (result.ResultOutcome.ToString().IndexOf("Fail_", StringComparison.OrdinalIgnoreCase) >= 0)
                    icon = MessageBoxIcon.Warning;

                MessageBox.Show(this,
                    "SFC finalizó\n\n" + (result.HumanSummary ?? "Revisa el log para el detalle."),
                    "SFC",
                    MessageBoxButtons.OK,
                    icon);
            }
            catch (OperationCanceledException)
            {
                _logger.Log("SFC: cancelado.");
                MessageBox.Show(this, "SFC fue cancelado.", "SFC", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _logger.Log("SFC: ERROR: " + ex.Message);
                MessageBox.Show(this, "Error en SFC:\n" + ex.Message, "SFC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                pb.Style = oldStyle;
                btnSfc.Enabled = AdminHelper.IsRunningAsAdmin();
                SetStatus("Listo.");
            }
        }

        // =========================
        // DISM
        // =========================
        private async void btnDism_Click(object sender, EventArgs e)
        {
            if (!AdminHelper.IsRunningAsAdmin())
            {
                MessageBox.Show(this, "DISM requiere permisos de Administrador.", "DISM",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshAdminUi();
                return;
            }

            btnDism.Enabled = false;
            var oldStyle = pb.Style;
            pb.Style = ProgressBarStyle.Marquee;
            SetStatus("DISM ejecutándose...");

            var cts = new CancellationTokenSource();

            try
            {
                _logger.Log("DISM: iniciando...");
                var result = await DismTask.RunAsync(true, _logger.Log, cts.Token).ConfigureAwait(true);

                _logger.Log("DISM: duración total: " + result.Duration);

                var icon = (result.ResultOutcome.ToString().IndexOf("Ok", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning;

                MessageBox.Show(this,
                    "DISM finalizó\n\n" + (result.HumanSummary ?? "Revisa el log para el detalle."),
                    "DISM",
                    MessageBoxButtons.OK,
                    icon);
            }
            catch (OperationCanceledException)
            {
                _logger.Log("DISM: cancelado.");
                MessageBox.Show(this, "DISM fue cancelado.", "DISM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _logger.Log("DISM: ERROR: " + ex.Message);
                MessageBox.Show(this, "Error en DISM:\n" + ex.Message, "DISM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                pb.Style = oldStyle;
                btnDism.Enabled = AdminHelper.IsRunningAsAdmin();
                SetStatus("Listo.");
            }
        }

        // =========================
        // WinUpdate FIX (usa WinUpdateFixTask)
        // =========================
        private async void btnWinUpdateFix_Click(object sender, EventArgs e)
        {
            if (!AdminHelper.IsRunningAsAdmin())
            {
                MessageBox.Show(this,
                    "Este módulo requiere permisos de Administrador.\n\nCierra y abre la app como Administrador.",
                    "WinUpdate",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                RefreshAdminUi();
                return;
            }

            // Bloquea acciones en paralelo
            SetBusy(true, "Reparando Windows Update...");

            // Re-habilitaremos manualmente al final
            btnWinUpdateFix.Enabled = false;

            var cts = new CancellationTokenSource();

            // Heartbeat UI friendly (WinForms Timer está bien aquí porque estamos en UI thread)
            var heartbeat = new System.Windows.Forms.Timer();
            heartbeat.Interval = 20000;
            heartbeat.Tick += (s, ev) => { _logger.Log("WinUpdate: sigo trabajando... (esto es normal)"); };
            heartbeat.Start();

            try
            {
                _logger.Log("WinUpdate: iniciando...");

                bool deepMode = true;

                // ✅ ACTUALIZADO: WinUpdateFixTask (sin using)
                var result = await WinUpdateFixTask.RunAsync(
                    isAdmin: true,
                    log: _logger.Log,
                    ct: cts.Token,
                    deepMode: deepMode
                ).ConfigureAwait(true);

                var icon = (result.Outcome == WinUpdateFixTask.WinUpdateOutcome.Ok)
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning;

                MessageBox.Show(
                    this,
                    "WinUpdate finalizó.\n\n" +
                    (result.HumanSummary ?? "Revisa el log para el detalle.") +
                    "\n\nDuración: " + result.Duration,
                    "WinUpdate",
                    MessageBoxButtons.OK,
                    icon
                );
            }
            catch (OperationCanceledException)
            {
                _logger.Log("WinUpdate: operación cancelada.");
                MessageBox.Show(this,
                    "WinUpdate fue cancelado.\nRevisa el log para el detalle.",
                    "WinUpdate",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                _logger.Log("WinUpdate: ERROR -> " + ex.Message);
                MessageBox.Show(this,
                    "Ocurrió un error al ejecutar WinUpdate.\n\n" + ex.Message,
                    "WinUpdate",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                try { heartbeat.Stop(); } catch { }
                try { heartbeat.Dispose(); } catch { }

                SetBusy(false, "Listo.");
                RefreshAdminUi();
                btnWinUpdateFix.Enabled = AdminHelper.IsRunningAsAdmin();

                _logger.Log("WinUpdate: listo.");
                SetStatus("Listo.");
            }
        }

        // =========================
        // Drivers rescan
        // =========================
        private async void btnDriversRescan_Click(object sender, EventArgs e)
        {
            if (!AdminHelper.IsRunningAsAdmin())
            {
                MessageBox.Show(this, "Drivers rescan requiere permisos de Administrador.", "Drivers",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshAdminUi();
                return;
            }

            btnDriversRescan.Enabled = false;
            var oldStyle = pb.Style;
            pb.Style = ProgressBarStyle.Marquee;
            SetStatus("Re-escaneo de hardware en progreso...");

            var cts = new CancellationTokenSource();

            try
            {
                _logger.Log("Drivers: iniciando...");
                var result = await DriversRescanTask.RunAsync(true, _logger.Log, cts.Token).ConfigureAwait(true);

                _logger.Log("Drivers: duración total: " + result.Duration);

                var icon = (result.ResultOutcome.ToString().IndexOf("Ok", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning;

                MessageBox.Show(this,
                    "Drivers finalizó\n\n" + (result.HumanSummary ?? "Revisa el log para el detalle."),
                    "Drivers",
                    MessageBoxButtons.OK,
                    icon);
            }
            catch (OperationCanceledException)
            {
                _logger.Log("Drivers: cancelado.");
                MessageBox.Show(this, "Drivers fue cancelado.", "Drivers", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _logger.Log("Drivers: ERROR: " + ex.Message);
                MessageBox.Show(this, "Error en Drivers:\n" + ex.Message, "Drivers", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                pb.Style = oldStyle;
                btnDriversRescan.Enabled = AdminHelper.IsRunningAsAdmin();
                SetStatus("Listo.");
            }
        }

        // =========================
        // Event Report (quitar using(...) y dejar igual lógica)
        // =========================
        private void btnEventReport_Click(object sender, EventArgs e)
        {
            var selectedLogs = clbLogs.CheckedItems.Cast<object>().Select(x => x.ToString()).ToList();

            var selectedLevelsText = clbLevels.CheckedItems.Cast<object>().Select(x => x.ToString()).ToList();
            var levels = new List<EventLogReport.Level>();

            foreach (var t in selectedLevelsText)
            {
                if (t.IndexOf("Crítico", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Critical);
                else if (t.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Error);
                else if (t.IndexOf("Advertencia", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Warning);
                else if (t.IndexOf("Información", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Information);
                else if (t.IndexOf("Detallado", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         t.IndexOf("Verbose", StringComparison.OrdinalIgnoreCase) >= 0) levels.Add(EventLogReport.Level.Verbose);
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

            int days = 7;
            string range = (cmbRange.SelectedItem != null ? cmbRange.SelectedItem.ToString() : "Últimos 7 días");
            if (range.IndexOf("24", StringComparison.OrdinalIgnoreCase) >= 0) days = 1;
            else if (range.IndexOf("30", StringComparison.OrdinalIgnoreCase) >= 0) days = 30;
            else days = 7;

            DateTime fromUtc = DateTime.UtcNow.AddDays(-days);

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

            SaveFileDialog sfd = null;
            try
            {
                sfd = new SaveFileDialog();
                sfd.Title = "Guardar reporte";
                sfd.Filter = "Reporte TXT (*.txt)|*.txt|Reporte CSV (*.csv)|*.csv";
                sfd.FileName = "EventReport_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                if (sfd.ShowDialog(this) != DialogResult.OK)
                    return;

                string path = sfd.FileName;
                bool exportCsv = (sfd.FilterIndex == 2) || path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

                btnEventReport.Enabled = false;
                pb.Style = ProgressBarStyle.Marquee;
                SetStatus("Generando reporte de eventos...");

                _logger.Log("EventReport: iniciando...");
                _logger.Log("Logs: " + string.Join(", ", selectedLogs));
                _logger.Log("Niveles: " + string.Join(", ", levels.Select(l => l.ToString())));
                _logger.Log("Rango: " + range + " | Desde (UTC): " + fromUtc.ToString("o"));
                _logger.Log("Máx eventos: " + maxEvents + " | Mensaje completo: " + (fullMsg ? "Sí" : "No"));

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

                        result = EventLogReport.Generate(opt, CancellationToken.None, (count) => { });

                        if (exportCsv) EventLogReport.ExportCsv(result, path);
                        else EventLogReport.ExportTxt(result, path);
                    }
                    catch (Exception ex)
                    {
                        caught = ex;
                    }

                    BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            if (caught != null)
                            {
                                _logger.Log("EventReport: error ❌ " + caught.Message);
                                SetStatus("Error generando reporte.");
                                MessageBox.Show(this, "Error generando reporte:\n\n" + caught, "Event Report",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                                return;
                            }

                            if (result != null && result.Warnings.Count > 0)
                            {
                                foreach (var w in result.Warnings)
                                    _logger.Log("EventReport [WARN] " + w);
                            }

                            _logger.Log("EventReport: listo ✅");
                            _logger.Log("Guardado en: " + path);
                            _logger.Log("Eventos exportados: " + (result != null ? result.Entries.Count.ToString() : "0"));

                            MessageBox.Show(this,
                                "Reporte generado.\n\n" +
                                "Archivo: " + path + "\n" +
                                "Eventos: " + (result != null ? result.Entries.Count.ToString() : "0"),
                                "Event Report",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);

                            SetStatus("Reporte generado.");
                        }
                        finally
                        {
                            pb.Style = ProgressBarStyle.Blocks;
                            btnEventReport.Enabled = true;
                            SetStatus("Listo.");
                        }
                    }));
                });
            }
            finally
            {
                if (sfd != null)
                {
                    try { sfd.Dispose(); } catch { }
                }
            }
        }

        // =========================
        // Exportar log (sin using)
        // =========================
        private void btnExportLog_Click(object sender, EventArgs e)
        {
            SaveFileDialog sfd = null;
            try
            {
                sfd = new SaveFileDialog();
                sfd.Title = "Guardar log";
                sfd.Filter = "Archivo de texto (*.txt)|*.txt";
                sfd.FileName = "Log_MiniMantenimiento_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt";

                if (sfd.ShowDialog(this) != DialogResult.OK)
                    return;

                File.WriteAllText(sfd.FileName, _log.ToString(), Encoding.UTF8);
                _logger.Log("Log exportado: " + sfd.FileName);
                SetStatus("Log exportado.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo exportar el log.\n\n" + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("Error exportando log.");
            }
            finally
            {
                if (sfd != null)
                {
                    try { sfd.Dispose(); } catch { }
                }
                SetStatus("Listo.");
            }
        }
    }
}
