// Services/NetworkResetService.cs
// Versión nueva (más limpia):
// - Quita netsh int ipv4 reset / ipv6 reset (salida legacy fea y aporta poco hoy)
// - Mantiene lo importante: flushdns + winsock reset + ip reset (con log) + release/renew (opcional)
// - Todo corre por CommandRunner.RunExe (sin cmd, sin chcp, sin líos de encoding)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MiniMantenimiento.Services
{
    internal sealed class NetworkResetService
    {
        private readonly CommandRunner _runner;

        public NetworkResetService(CommandRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public sealed class Step
        {
            public string Title;
            public string FileName;
            public string Arguments;
            public CommandRunner.CmdResult Result;
        }

        public sealed class NetworkResetResult
        {
            public List<Step> Steps = new List<Step>();
            public bool AnyPartial;
            public bool AnyCritical;
            public string IpResetLogPath;

            public int TotalSteps => Steps?.Count ?? 0;
            public int FailedSteps => Steps?.Count(s => s?.Result != null && s.Result.ExitCode != 0) ?? 0;
        }

        /// <summary>
        /// Ejecuta reset de red "práctico" y con output legible.
        /// deepMode=true agrega un reset más agresivo (netcfg -d) que suele requerir reinicio.
        /// </summary>
        public NetworkResetResult Run(int timeoutMsPerCommand = 120000, bool deepMode = false)
        {
            var res = new NetworkResetResult
            {
                IpResetLogPath = Path.Combine(Path.GetTempPath(), "ipreset.log")
            };

            int T = timeoutMsPerCommand;

            // 1) DNS cache
            Add(res, "ipconfig /flushdns", "ipconfig.exe", "/flushdns",
                _runner.RunExe("ipconfig.exe", "/flushdns", T));

            // 2) Winsock
            Add(res, "netsh winsock reset", "netsh.exe", "winsock reset",
                _runner.RunExe("netsh.exe", "winsock reset", T));

            // 3) IP reset con log (principal)
            Add(res, "netsh int ip reset (log)", "netsh.exe",
                $"int ip reset \"{res.IpResetLogPath}\"",
                _runner.RunExe("netsh.exe", $"int ip reset \"{res.IpResetLogPath}\"", T));

            // 4) Release / Renew (opcional pero útil)
            Add(res, "ipconfig /release", "ipconfig.exe", "/release",
                _runner.RunExe("ipconfig.exe", "/release", T));

            Add(res, "ipconfig /renew", "ipconfig.exe", "/renew",
                _runner.RunExe("ipconfig.exe", "/renew", T));

            // 5) Modo profundo (agresivo): reinstala adaptadores / reset stack
            // NOTA: suele romper VPNs/bridges virtuales momentáneamente y requiere reinicio.
            if (deepMode)
            {
                Add(res, "netcfg -d (deep)", "netcfg.exe", "-d",
                    _runner.RunExe("netcfg.exe", "-d", T));
            }

            Analyze(res);
            return res;
        }

        private static void Analyze(NetworkResetResult res)
        {
            res.AnyPartial = false;
            res.AnyCritical = false;

            foreach (var s in res.Steps)
            {
                var r = s?.Result;
                if (r == null) continue;

                // timeout/cancel = crítico
                if (r.TimedOut || r.Canceled)
                {
                    res.AnyCritical = true;
                    continue;
                }

                if (r.ExitCode != 0)
                {
                    // Acceso denegado suele ser parcial en netsh
                    if (CommandRunner.IsPartialSuccess(r))
                        res.AnyPartial = true;
                    else
                        res.AnyCritical = true;
                }
            }
        }

        private static void Add(NetworkResetResult res, string title, string fileName, string args, CommandRunner.CmdResult r)
        {
            res.Steps.Add(new Step
            {
                Title = title,
                FileName = fileName,
                Arguments = args,
                Result = r
            });
        }
    }
}
