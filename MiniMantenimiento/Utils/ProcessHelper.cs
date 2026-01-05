using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace MiniMantenimiento.Utils
{
    internal class ProcessHelper
    {
        public static void RunDetached(string fileName, string args = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args ?? "",
                UseShellExecute = true,   // para abrir .msc
                CreateNoWindow = false
            };

            Process.Start(psi);
        }
    }

}
