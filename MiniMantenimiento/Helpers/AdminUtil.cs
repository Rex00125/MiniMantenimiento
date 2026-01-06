// Helpers/AdminUtil.cs
using System.Security.Principal;

namespace MiniMantenimiento.Helpers
{
    internal static class AdminUtil
    {
        internal static bool IsRunningAsAdmin()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }
    }
}
