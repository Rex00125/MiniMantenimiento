// Services/CommandRunner.cs
// Runner definitivo:
// - Lee BYTES crudos de stdout/stderr (sin BeginOutputReadLine, sin StreamReader.ReadToEnd)
// - Detecta encoding de forma robusta (BOM -> UTF-16/UTF-8; si no hay BOM -> valida UTF-8; si no -> score)
// - Devuelve texto legible en español para netsh/ipconfig/sfc/dism, etc.
// - Timeout + cancelación

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniMantenimiento.Services
{
    internal sealed class CommandRunner
    {
        // Encodings candidatos comunes en Windows ES
        private static readonly Encoding Oem850 = Encoding.GetEncoding(850);
        private static readonly Encoding Win1252 = Encoding.GetEncoding(1252);
        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static readonly Encoding Utf16LE = Encoding.Unicode;   // UTF-16LE
        private static readonly Encoding Utf16BE = Encoding.BigEndianUnicode;

        public sealed class CmdResult
        {
            public int ExitCode;
            public string StdOut;
            public string StdErr;
            public bool TimedOut;
            public bool Canceled;
        }

        public CmdResult RunExe(string fileName, string arguments, int timeoutMs)
            => RunExe(fileName, arguments, timeoutMs, CancellationToken.None);

        public CmdResult RunExe(string fileName, string arguments, int timeoutMs, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var p = new Process())
            {
                p.StartInfo = psi;

                try { p.Start(); }
                catch (Exception ex)
                {
                    return new CmdResult
                    {
                        ExitCode = -2,
                        StdOut = "",
                        StdErr = "No se pudo iniciar el proceso: " + ex.Message
                    };
                }

                // Leer bytes crudos (IMPORTANTÍSIMO)
                Task<byte[]> tOut = Task.Run(() => ReadAllBytes(p.StandardOutput.BaseStream));
                Task<byte[]> tErr = Task.Run(() => ReadAllBytes(p.StandardError.BaseStream));

                int startTick = Environment.TickCount;

                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        TryKill(p);
                        return new CmdResult
                        {
                            ExitCode = -3,
                            StdOut = DecodeSmart(SafeGet(tOut)),
                            StdErr = DecodeSmart(SafeGet(tErr)),
                            Canceled = true
                        };
                    }

                    if (p.HasExited) break;

                    if (timeoutMs > 0 && unchecked(Environment.TickCount - startTick) >= timeoutMs)
                    {
                        TryKill(p);
                        return new CmdResult
                        {
                            ExitCode = -1,
                            StdOut = DecodeSmart(SafeGet(tOut)),
                            StdErr = "TIMEOUT: El comando tardó demasiado.\n" + DecodeSmart(SafeGet(tErr)),
                            TimedOut = true
                        };
                    }

                    Thread.Sleep(50);
                }

                return new CmdResult
                {
                    ExitCode = p.ExitCode,
                    StdOut = DecodeSmart(SafeGet(tOut)),
                    StdErr = DecodeSmart(SafeGet(tErr))
                };
            }
        }

        // -------------------------
        // Decodificación robusta
        // -------------------------
        private static string DecodeSmart(byte[] data)
        {
            if (data == null || data.Length == 0) return "";

            // 1) BOM: manda
            if (HasBomUtf16LE(data)) return Utf16LE.GetString(data, 2, data.Length - 2);
            if (HasBomUtf16BE(data)) return Utf16BE.GetString(data, 2, data.Length - 2);
            if (HasBomUtf8(data)) return Encoding.UTF8.GetString(data, 3, data.Length - 3);

            // 2) Si parece UTF-16 por patrón de ceros (mejorado)
            if (LooksLikeUtf16LE(data)) return Utf16LE.GetString(data);
            if (LooksLikeUtf16BE(data)) return Utf16BE.GetString(data);

            // 3) Si es UTF-8 válido, úsalo
            if (IsValidUtf8(data))
            {
                try { return Utf8.GetString(data); }
                catch { /* caerá a scoring */ }
            }

            // 4) Score entre OEM850 y Win1252 (y fallback UTF-8 sin throw)
            string s850 = Oem850.GetString(data);
            string s1252 = Win1252.GetString(data);
            string sUtf8Loose = Encoding.UTF8.GetString(data); // sin throw

            var best = PickBest(s850, s1252, sUtf8Loose);
            return best;
        }

        private static string PickBest(params string[] candidates)
        {
            int bestScore = int.MinValue;
            string best = candidates.Length > 0 ? candidates[0] : "";

            foreach (var s in candidates)
            {
                int sc = ScoreHumanText(s);
                if (sc > bestScore)
                {
                    bestScore = sc;
                    best = s;
                }
            }
            return best;
        }

        // Score simple: premia letras/espacios/puntuación, castiga caracteres raros/control/�/muchos | sueltos
        private static int ScoreHumanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return int.MinValue;

            int score = 0;
            int weird = 0;

            foreach (char c in s)
            {
                if (c == '\uFFFD') { score -= 20; weird++; continue; } // replacement char
                if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') { score -= 10; weird++; continue; }

                if (char.IsLetterOrDigit(c)) score += 2;
                else if (c == ' ' || c == '\r' || c == '\n' || c == '\t') score += 1;
                else if (".,;:¡!¿?()[]{}-_\"'\\/|@#%&+=*".IndexOf(c) >= 0) score += 0; // neutro
                else score -= 2; // raro
            }

            // Castigo extra si hay demasiados '|' (tu síntoma)
            int pipes = 0;
            foreach (char c in s) if (c == '|') pipes++;
            if (pipes >= 3) score -= pipes * 3;

            // Premia presencia de caracteres típicos del español
            if (s.IndexOf('ó') >= 0 || s.IndexOf('á') >= 0 || s.IndexOf('é') >= 0 ||
                s.IndexOf('í') >= 0 || s.IndexOf('ú') >= 0 || s.IndexOf('ñ') >= 0)
                score += 20;

            // Penaliza si “casi todo” es raro
            if (weird > 0 && weird * 5 > s.Length) score -= 50;

            return score;
        }

        private static bool HasBomUtf16LE(byte[] d) => d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE;
        private static bool HasBomUtf16BE(byte[] d) => d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF;
        private static bool HasBomUtf8(byte[] d) => d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF;

        private static bool LooksLikeUtf16LE(byte[] d)
        {
            // Cuenta ceros en bytes impares; ratio > 20% en los primeros 400 bytes
            int n = Math.Min(d.Length, 400);
            if (n < 20) return false;

            int zeros = 0, checkedCount = 0;
            for (int i = 1; i < n; i += 2) { checkedCount++; if (d[i] == 0x00) zeros++; }

            double ratio = checkedCount > 0 ? (zeros / (double)checkedCount) : 0;
            return ratio > 0.20;
        }

        private static bool LooksLikeUtf16BE(byte[] d)
        {
            // Ceros en bytes pares
            int n = Math.Min(d.Length, 400);
            if (n < 20) return false;

            int zeros = 0, checkedCount = 0;
            for (int i = 0; i < n; i += 2) { checkedCount++; if (d[i] == 0x00) zeros++; }

            double ratio = checkedCount > 0 ? (zeros / (double)checkedCount) : 0;
            return ratio > 0.20;
        }

        private static bool IsValidUtf8(byte[] d)
        {
            try { Utf8.GetString(d); return true; }
            catch { return false; }
        }

        private static byte[] ReadAllBytes(Stream s)
        {
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static T SafeGet<T>(Task<T> t)
        {
            try { return t != null ? t.GetAwaiter().GetResult() : default; }
            catch { return default; }
        }

        private static void TryKill(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(); }
            catch { }
        }

        public static bool IsPartialSuccess(CmdResult r)
        {
            if (r == null || r.ExitCode == 0) return false;

            string t = ((r.StdOut ?? "") + "\n" + (r.StdErr ?? "")).ToLowerInvariant();
            return t.Contains("acceso denegado") || t.Contains("access is denied");
        }
    }
}
