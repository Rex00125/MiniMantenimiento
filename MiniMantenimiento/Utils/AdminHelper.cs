using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

namespace MiniMantenimiento.Utils
{
    internal static class AdminHelper
    {
        /// <summary>
        /// Indica si el proceso actual se está ejecutando como Administrador.
        /// </summary>
        internal static bool IsRunningAsAdmin()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                if (identity == null) return false;

                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Relanza la aplicación actual solicitando elevación (UAC).
        /// Devuelve true si se intentó relanzar.
        /// </summary>
        internal static bool RelaunchAsAdmin()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                if (string.IsNullOrWhiteSpace(exePath))
                    return false;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas" // <- UAC
                };

                Process.Start(psi);
                return true;
            }
            catch
            {
                // Usuario canceló UAC o error
                return false;
            }
        }

        /// <summary>
        /// Asegura que la app esté corriendo como admin.
        /// Si no lo está, pregunta y relanza.
        /// Devuelve true si ya es admin o si se relanzó.
        /// </summary>
        internal static bool EnsureAdmin(IWin32Window owner = null)
        {
            if (IsRunningAsAdmin())
                return true;

            DialogResult dr = MessageBox.Show(
                owner,
                "Esta acción requiere permisos de Administrador.\n\n¿Deseas reiniciar la aplicación como Administrador?",
                "Permisos requeridos",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (dr != DialogResult.Yes)
                return false;

            bool relaunched = RelaunchAsAdmin();
            if (relaunched)
            {
                try { Application.Exit(); } catch { }
            }

            return relaunched;
        }
    }
}
