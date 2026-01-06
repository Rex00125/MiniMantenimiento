using System;
using System.Diagnostics;

namespace MiniMantenimiento.Utils
{
    internal static class ProcessHelper
    {
        /// <summary>
        /// Ejecuta un proceso desacoplado (no espera resultado).
        /// Útil para .msc, .cpl, herramientas del sistema, etc.
        /// </summary>
        public static bool RunDetached(string fileName, string args = null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args ?? "",
                    UseShellExecute = true,   // requerido para .msc, .cpl
                    CreateNoWindow = false
                };

                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Ejecuta un proceso desacoplado solicitando elevación (UAC).
        /// </summary>
        public static bool RunDetachedAsAdmin(string fileName, string args = null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args ?? "",
                    UseShellExecute = true,
                    Verb = "runas",          // UAC
                    CreateNoWindow = false
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
        /// Abre una consola CMD independiente (opcionalmente como admin).
        /// </summary>
        public static bool OpenCmd(bool asAdmin)
        {
            return asAdmin
                ? RunDetachedAsAdmin("cmd.exe")
                : RunDetached("cmd.exe");
        }

        /// <summary>
        /// Abre PowerShell independiente (opcionalmente como admin).
        /// </summary>
        public static bool OpenPowerShell(bool asAdmin)
        {
            return asAdmin
                ? RunDetachedAsAdmin("powershell.exe")
                : RunDetached("powershell.exe");
        }

        /// <summary>
        /// Abre una consola MMC (.msc), por ejemplo:
        /// services.msc, devmgmt.msc, compmgmt.msc
        /// </summary>
        public static bool OpenMsc(string mscName, bool asAdmin)
        {
            if (string.IsNullOrWhiteSpace(mscName))
                return false;

            // mmc.exe services.msc
            return asAdmin
                ? RunDetachedAsAdmin("mmc.exe", mscName)
                : RunDetached("mmc.exe", mscName);
        }

        /// <summary>
        /// Abre un panel de control clásico (.cpl).
        /// Ej: appwiz.cpl, ncpa.cpl
        /// </summary>
        public static bool OpenCpl(string cplName)
        {
            if (string.IsNullOrWhiteSpace(cplName))
                return false;

            return RunDetached("control.exe", cplName);
        }

        /// <summary>
        /// Abre una URL en el navegador por defecto.
        /// </summary>
        public static bool OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
