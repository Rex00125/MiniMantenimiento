// Services/HealthCheckService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MiniMantenimiento.Services
{
    internal sealed class HealthCheckService
    {
        public List<string> BuildHealthCheckLines()
        {
            var lines = new List<string>();

            lines.Add("===== CHEQUEO RÁPIDO =====");
            lines.Add("Equipo: " + Environment.MachineName);
            lines.Add("Usuario: " + Environment.UserName);
            lines.Add("Número de serie: " + GetSerialNumber());
            lines.Add("Windows: " + GetWindowsFriendlyName());
            lines.Add("Arquitectura: " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
            lines.Add("Uptime: " + FormatUptime());

            string cpu = GetCpuName();
            if (!string.IsNullOrWhiteSpace(cpu))
                lines.Add("CPU: " + cpu);

            var ram = GetRamInfo();
            if (ram.TotalBytes > 0)
            {
                lines.Add($"RAM: Total {FormatBytes(ram.TotalBytes)} | Disponible {FormatBytes(ram.AvailableBytes)}");
            }

            lines.Add("Discos:");
            foreach (var d in GetDiskInfo())
                lines.Add("  - " + d);

            lines.Add("Red:");
            foreach (var n in GetNetworkInfo())
                lines.Add("  - " + n);

            lines.Add("==========================");
            return lines;
        }

        // ------------------------
        // Windows / Hardware
        // ------------------------

        private static string GetWindowsFriendlyName()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Caption, Version FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject os in searcher.Get())
                    {
                        string caption = os["Caption"]?.ToString().Trim();
                        string version = os["Version"]?.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(caption))
                            return caption + (string.IsNullOrWhiteSpace(version) ? "" : $" (v{version})");
                    }
                }
            }
            catch { }

            return Environment.OSVersion.VersionString;
        }

        private static string GetSerialNumber()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber FROM Win32_BIOS"))
                {
                    foreach (ManagementObject bios in searcher.Get())
                    {
                        string serial = bios["SerialNumber"] as string;
                        if (!string.IsNullOrWhiteSpace(serial))
                        {
                            serial = serial.Trim();
                            if (serial.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
                                serial.IndexOf("O.E.M", StringComparison.OrdinalIgnoreCase) >= 0)
                                return "(no definido por el fabricante)";
                            return serial;
                        }
                    }
                }
            }
            catch { }

            return "(no disponible)";
        }

        private static string GetCpuName()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject cpu in searcher.Get())
                    {
                        var name = cpu["Name"] as string;
                        if (!string.IsNullOrWhiteSpace(name))
                            return name.Trim();
                    }
                }
            }
            catch { }

            return "";
        }

        private static (ulong TotalBytes, ulong AvailableBytes) GetRamInfo()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject os in searcher.Get())
                    {
                        ulong totalKb = Convert.ToUInt64(os["TotalVisibleMemorySize"] ?? 0);
                        ulong freeKb = Convert.ToUInt64(os["FreePhysicalMemory"] ?? 0);
                        return (totalKb * 1024UL, freeKb * 1024UL);
                    }
                }
            }
            catch { }

            return (0, 0);
        }

        // ------------------------
        // Discos
        // ------------------------

        private static IEnumerable<string> GetDiskInfo()
        {
            var list = new List<string>();

            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;

                    long total = drive.TotalSize;
                    long free = drive.AvailableFreeSpace;
                    double pctFree = total > 0 ? free * 100.0 / total : 0;

                    string alert = "";
                    if (drive.DriveType == DriveType.Fixed)
                    {
                        if (pctFree < 10 || free < 10L * 1024 * 1024 * 1024)
                            alert = " ⚠️ ESPACIO BAJO";
                    }

                    list.Add(
                        $"{drive.Name} ({drive.DriveType}) - Libre: {FormatBytes((ulong)free)} / " +
                        $"{FormatBytes((ulong)total)} ({pctFree:0.0}% libre){alert}"
                    );
                }
            }
            catch (Exception ex)
            {
                list.Add("Error al leer discos: " + ex.Message);
            }

            if (list.Count == 0)
                list.Add("No se pudieron enumerar discos.");

            return list;
        }

        // ------------------------
        // Red
        // ------------------------

        private static IEnumerable<string> GetNetworkInfo()
        {
            var list = new List<string>();

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var ipProps = ni.GetIPProperties();

                    var ipv4s = ipProps.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString())
                        .ToList();

                    if (ipv4s.Count == 0) continue;

                    var gw = ipProps.GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(a => a != null && a.AddressFamily == AddressFamily.InterNetwork);

                    var dns = ipProps.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .Take(3)
                        .ToList();

                    string speed = ni.Speed > 0
                        ? $"{ni.Speed / 1_000_000} Mbps"
                        : "desconocida";

                    string line =
                        $"{ni.Name} [{ni.NetworkInterfaceType}, {speed}] | IP: {string.Join(", ", ipv4s)}";

                    if (gw != null) line += " | GW: " + gw;
                    if (dns.Count > 0) line += " | DNS: " + string.Join(", ", dns);

                    list.Add(line);
                }
            }
            catch (Exception ex)
            {
                list.Add("Error al leer red: " + ex.Message);
            }

            if (list.Count == 0)
                list.Add("No se detectó interfaz activa con IPv4.");

            return list;
        }

        // ------------------------
        // Utilidades
        // ------------------------

        private static string FormatBytes(ulong bytes)
        {
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (b >= 1024 && u < units.Length - 1)
            {
                b /= 1024;
                u++;
            }
            return $"{b:0.##} {units[u]}";
        }

        private static string FormatUptime()
        {
            try
            {
                long ms = Environment.TickCount;
                var ts = TimeSpan.FromMilliseconds(ms);

                if (ts.TotalDays >= 1)
                    return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m";
                if (ts.TotalHours >= 1)
                    return $"{(int)ts.TotalHours}h {ts.Minutes}m";
                return $"{ts.Minutes}m {ts.Seconds}s";
            }
            catch
            {
                return "(no disponible)";
            }
        }
    }
}
