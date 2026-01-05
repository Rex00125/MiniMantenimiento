// Form1.Designer.cs
using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace MiniMantenimiento
{
    partial class Form1
    {
        private IContainer components = null;

        private Label lblAdminStatus;

        // ✅ NUEVO: Status “humano” para operaciones largas (DISM/SFC/etc.)
        private Label lblStatus;

        private GroupBox gbAcciones;
        private Button btnHealthCheck;
        private Button btnNetReset;
        private Button btnCleanup;
        private CheckBox chkEmptyRecycleBin;
        private Button btnSfc;
        private Button btnDism;
        private Button btnWinUpdateFix;
        private Button btnDriversRescan;

        private GroupBox gbEventReport;
        private Label lblLogs;
        private CheckedListBox clbLogs;
        private Label lblLevels;
        private CheckedListBox clbLevels;
        private Label lblRange;
        private ComboBox cmbRange;
        private Label lblMaxEvents;
        private NumericUpDown nudMaxEvents;
        private CheckBox chkFullMessage;
        private Button btnEventReport;

        private GroupBox gbSalida;
        private TextBox txtLog;
        private ProgressBar pb;
        private Button btnExportLog;

        private ToolTip toolTip1;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new Container();
            this.toolTip1 = new ToolTip(this.components);

            this.lblAdminStatus = new Label();

            // ✅ NUEVO
            this.lblStatus = new Label();

            this.gbAcciones = new GroupBox();
            this.btnHealthCheck = new Button();
            this.btnNetReset = new Button();
            this.btnCleanup = new Button();
            this.chkEmptyRecycleBin = new CheckBox();
            this.btnSfc = new Button();
            this.btnDism = new Button();
            this.btnWinUpdateFix = new Button();
            this.btnDriversRescan = new Button();

            this.gbEventReport = new GroupBox();
            this.lblLogs = new Label();
            this.clbLogs = new CheckedListBox();
            this.lblLevels = new Label();
            this.clbLevels = new CheckedListBox();
            this.lblRange = new Label();
            this.cmbRange = new ComboBox();
            this.lblMaxEvents = new Label();
            this.nudMaxEvents = new NumericUpDown();
            this.chkFullMessage = new CheckBox();
            this.btnEventReport = new Button();

            this.gbSalida = new GroupBox();
            this.txtLog = new TextBox();
            this.pb = new ProgressBar();
            this.btnExportLog = new Button();

            ((ISupportInitialize)(this.nudMaxEvents)).BeginInit();
            this.SuspendLayout();

            // ===== FORM =====
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(1120, 720);
            this.MinimumSize = new Size(980, 640);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "Mini Mantenimiento (Casual)";
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;

            // ===== lblAdminStatus =====
            this.lblAdminStatus.Location = new Point(12, 10);
            this.lblAdminStatus.Size = new Size(1096, 22);
            this.lblAdminStatus.Text = "Estado:";
            this.lblAdminStatus.TextAlign = ContentAlignment.MiddleLeft;

            // ===== gbAcciones =====
            this.gbAcciones.Text = "Acciones";
            this.gbAcciones.Location = new Point(12, 40);
            this.gbAcciones.Size = new Size(340, 360);
            this.gbAcciones.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            // btnHealthCheck
            this.btnHealthCheck.Text = "🩺 Chequeo rápido";
            this.btnHealthCheck.Location = new Point(16, 28);
            this.btnHealthCheck.Size = new Size(308, 34);
            this.btnHealthCheck.TabIndex = 0;
            this.btnHealthCheck.Click += new EventHandler(this.btnHealthCheck_Click);
            this.toolTip1.SetToolTip(this.btnHealthCheck, "Muestra un resumen rápido del estado del equipo.");

            // btnNetReset
            this.btnNetReset.Text = "🛜 Reset de red (Winsock/DNS)";
            this.btnNetReset.Location = new Point(16, 72);
            this.btnNetReset.Size = new Size(308, 34);
            this.btnNetReset.TabIndex = 1;
            this.btnNetReset.Click += new EventHandler(this.btnNetReset_Click);
            this.toolTip1.SetToolTip(this.btnNetReset, "FlushDNS + reset winsock/ip. Puede requerir admin.");

            // btnCleanup
            this.btnCleanup.Text = "🧹 Limpiar temporales";
            this.btnCleanup.Location = new Point(16, 116);
            this.btnCleanup.Size = new Size(308, 34);
            this.btnCleanup.TabIndex = 2;
            this.btnCleanup.Click += new EventHandler(this.btnCleanup_Click);
            this.toolTip1.SetToolTip(this.btnCleanup, "Limpia temporales del usuario y del sistema (si hay permisos).");

            // chkEmptyRecycleBin
            this.chkEmptyRecycleBin.Text = "Vaciar Papelera (opcional)";
            this.chkEmptyRecycleBin.Location = new Point(20, 156);
            this.chkEmptyRecycleBin.AutoSize = true;
            this.chkEmptyRecycleBin.TabIndex = 3;
            this.toolTip1.SetToolTip(this.chkEmptyRecycleBin, "Vacía la Papelera de reciclaje. Puede tardar si está muy llena.");

            // btnSfc
            this.btnSfc.Text = "🧱 Reparar archivos (SFC)";
            this.btnSfc.Location = new Point(16, 184);
            this.btnSfc.Size = new Size(308, 34);
            this.btnSfc.TabIndex = 4;
            this.btnSfc.Click += new EventHandler(this.btnSfc_Click);
            this.toolTip1.SetToolTip(this.btnSfc, "Ejecuta: sfc /scannow (requiere admin).");

            // btnDism
            this.btnDism.Text = "🧰 Reparar imagen (DISM)";
            this.btnDism.Location = new Point(16, 228);
            this.btnDism.Size = new Size(308, 34);
            this.btnDism.TabIndex = 5;
            this.btnDism.Click += new EventHandler(this.btnDism_Click);
            this.toolTip1.SetToolTip(this.btnDism, "DISM RestoreHealth (requiere admin).");

            // btnWinUpdateFix
            this.btnWinUpdateFix.Text = "🔄 Reparar Windows Update";
            this.btnWinUpdateFix.Location = new Point(16, 272);
            this.btnWinUpdateFix.Size = new Size(308, 34);
            this.btnWinUpdateFix.TabIndex = 6;
            this.btnWinUpdateFix.Click += new EventHandler(this.btnWinUpdateFix_Click);

            // btnDriversRescan
            this.btnDriversRescan.Text = "🧩 Re-escaneo de hardware (drivers)";
            this.btnDriversRescan.Location = new Point(16, 316);
            this.btnDriversRescan.Size = new Size(308, 34);
            this.btnDriversRescan.TabIndex = 7;
            this.btnDriversRescan.Click += new EventHandler(this.btnDriversRescan_Click);

            this.gbAcciones.Controls.AddRange(new Control[]
            {
                this.btnHealthCheck,
                this.btnNetReset,
                this.btnCleanup,
                this.chkEmptyRecycleBin,
                this.btnSfc,
                this.btnDism,
                this.btnWinUpdateFix,
                this.btnDriversRescan
            });

            // ===== gbEventReport =====
            this.gbEventReport.Text = "Reporte del Visor de eventos";
            this.gbEventReport.Location = new Point(12, 410);
            this.gbEventReport.Size = new Size(340, 298);
            this.gbEventReport.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // lblLogs
            this.lblLogs.Text = "Logs:";
            this.lblLogs.Location = new Point(16, 28);
            this.lblLogs.AutoSize = true;

            // clbLogs
            this.clbLogs.Location = new Point(16, 48);
            this.clbLogs.Size = new Size(308, 60);
            this.clbLogs.CheckOnClick = true;

            // lblLevels
            this.lblLevels.Text = "Niveles:";
            this.lblLevels.Location = new Point(16, 114);
            this.lblLevels.AutoSize = true;

            // clbLevels
            this.clbLevels.Location = new Point(16, 134);
            this.clbLevels.Size = new Size(308, 80);
            this.clbLevels.CheckOnClick = true;

            // lblRange
            this.lblRange.Text = "Rango de tiempo:";
            this.lblRange.Location = new Point(16, 220);
            this.lblRange.AutoSize = true;

            // cmbRange
            this.cmbRange.Location = new Point(16, 240);
            this.cmbRange.Size = new Size(140, 23);
            this.cmbRange.DropDownStyle = ComboBoxStyle.DropDownList;

            // lblMaxEvents
            this.lblMaxEvents.Text = "Máx. eventos:";
            this.lblMaxEvents.Location = new Point(170, 220);
            this.lblMaxEvents.AutoSize = true;

            // nudMaxEvents
            this.nudMaxEvents.Location = new Point(170, 240);
            this.nudMaxEvents.Size = new Size(154, 23);
            this.nudMaxEvents.Minimum = 1;
            this.nudMaxEvents.Maximum = 200000;
            this.nudMaxEvents.Value = 1000;

            // chkFullMessage
            this.chkFullMessage.Text = "Incluir mensaje completo";
            this.chkFullMessage.Location = new Point(16, 270);
            this.chkFullMessage.AutoSize = true;

            // btnEventReport
            this.btnEventReport.Text = "📄 Generar reporte";
            this.btnEventReport.Location = new Point(200, 268);
            this.btnEventReport.Size = new Size(124, 26);
            this.btnEventReport.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this.btnEventReport.Click += new EventHandler(this.btnEventReport_Click);

            this.gbEventReport.Controls.AddRange(new Control[]
            {
                this.lblLogs, this.clbLogs,
                this.lblLevels, this.clbLevels,
                this.lblRange, this.cmbRange,
                this.lblMaxEvents, this.nudMaxEvents,
                this.chkFullMessage, this.btnEventReport
            });

            // ===== gbSalida =====
            this.gbSalida.Text = "Salida / Log";
            this.gbSalida.Location = new Point(364, 40);
            this.gbSalida.Size = new Size(744, 668);
            this.gbSalida.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // txtLog
            this.txtLog.Location = new Point(16, 28);
            this.txtLog.Size = new Size(712, 560);
            this.txtLog.Multiline = true;
            this.txtLog.ScrollBars = ScrollBars.Vertical;
            this.txtLog.ReadOnly = true;
            this.txtLog.WordWrap = false;
            this.txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // ✅ NUEVO: lblStatus (entre log y progress bar)
            this.lblStatus.Location = new Point(16, 592);
            this.lblStatus.Size = new Size(712, 18);
            this.lblStatus.Text = "Listo.";
            this.lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            this.lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // pb
            this.pb.Location = new Point(16, 616);
            this.pb.Size = new Size(560, 22);
            this.pb.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // btnExportLog
            this.btnExportLog.Text = "💾 Exportar log";
            this.btnExportLog.Location = new Point(588, 612);
            this.btnExportLog.Size = new Size(140, 30);
            this.btnExportLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this.btnExportLog.Click += new EventHandler(this.btnExportLog_Click);

            this.gbSalida.Controls.AddRange(new Control[]
            {
                this.txtLog,
                this.lblStatus, // ✅ agregado
                this.pb,
                this.btnExportLog
            });

            // ===== ADD TO FORM =====
            this.Controls.Add(this.lblAdminStatus);
            this.Controls.Add(this.gbAcciones);
            this.Controls.Add(this.gbEventReport);
            this.Controls.Add(this.gbSalida);

            this.Load += new EventHandler(this.Form1_Load);

            ((ISupportInitialize)(this.nudMaxEvents)).EndInit();
            this.ResumeLayout(false);
        }
    }
}
