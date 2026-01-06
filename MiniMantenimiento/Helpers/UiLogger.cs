// Helpers/UiLogger.cs
using System;
using System.Text;
using System.Windows.Forms;
using MiniMantenimiento.Services;

namespace MiniMantenimiento.Helpers
{
    internal sealed class UiLogger
    {
        private readonly Control _uiOwner;
        private readonly TextBox _txtLog;
        private readonly StringBuilder _buffer;

        public UiLogger(Control uiOwner, TextBox txtLog, StringBuilder backingBuffer)
        {
            _uiOwner = uiOwner ?? throw new ArgumentNullException(nameof(uiOwner));
            _txtLog = txtLog ?? throw new ArgumentNullException(nameof(txtLog));
            _buffer = backingBuffer ?? throw new ArgumentNullException(nameof(backingBuffer));
        }

        public void Log(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            // ✅ Evita errores cuando el form se está cerrando
            if (_uiOwner.IsDisposed || !_uiOwner.IsHandleCreated)
                return;

            if (_uiOwner.InvokeRequired)
            {
                try { _uiOwner.BeginInvoke((Action)(() => Log(message))); }
                catch { }
                return;
            }

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            _buffer.AppendLine(line);
            _txtLog.AppendText(line + Environment.NewLine);
        }

        public void LogNetworkResetResult(NetworkResetService.NetworkResetResult result)
        {
            if (result == null) return;

            Log("===== RESET DE RED =====");

            foreach (var step in result.Steps)
                LogNetworkStep(step, result.IpResetLogPath);

            if (result.AnyCritical)
            {
                Log("Resultado: ⚠️ Se detectaron errores críticos en uno o más pasos.");
                Log("Recomendación: reinicia el equipo y revisa el log.");
            }
            else if (result.AnyPartial)
            {
                Log("Resultado: ⚠️ Éxito parcial con advertencias.");
                Log("Nota: es normal con adaptadores virtuales (VPN, Hyper-V, WSL).");
                Log("Recomendación: reinicia el equipo.");
            }
            else
            {
                Log("Resultado: ✅ Reset de red completado correctamente.");
                Log("Recomendación: reinicia el equipo para aplicar completamente los cambios.");
            }

            Log("========================");
        }

        private void LogNetworkStep(NetworkResetService.Step step, string ipResetLogPath)
        {
            if (step == null || step.Result == null)
                return;

            Log($"--- {step.Title} ---");
            Log($"ExitCode: {step.Result.ExitCode}");

            if (step.Title.StartsWith("netsh int ip reset", StringComparison.OrdinalIgnoreCase))
            {
                Log("Se restablecieron componentes de red.");

                if (step.Result.ExitCode != 0)
                    Log("Windows reportó advertencias (Acceso denegado en algún componente).");

                if (!string.IsNullOrWhiteSpace(ipResetLogPath))
                    Log("Detalle técnico guardado en: " + ipResetLogPath);

                return;
            }

            if (!string.IsNullOrWhiteSpace(step.Result.StdOut))
            {
                foreach (var line in SplitLines(step.Result.StdOut))
                    Log(line);
            }

            if (!string.IsNullOrWhiteSpace(step.Result.StdErr))
            {
                Log("[stderr]");
                foreach (var line in SplitLines(step.Result.StdErr))
                    Log(line);
            }
        }

        private static string[] SplitLines(string text)
        {
            return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        }

        public string GetFullLog() => _buffer.ToString();
    }
}
