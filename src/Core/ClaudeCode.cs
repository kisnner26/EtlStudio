using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EtlStudio.Core
{
    /// <summary>Cualquier motor de IA que la app pueda usar (API de pago o Claude Code con tu suscripcion).</summary>
    public interface ILlm
    {
        string Send(string system, IList<ClaudeClient.Turn> turns, int maxTokens = 8192);
    }

    public class ClaudeLoginRequiredException : Exception
    {
        public ClaudeLoginRequiredException(string m) : base(m) { }
    }

    /// <summary>
    /// Usa el Claude Code instalado en este equipo (<c>claude -p</c>) con TU sesion de suscripcion de Claude:
    /// no necesita API key ni credito de API. La app nunca ve ni guarda tus credenciales; solo ejecuta el programa.
    /// </summary>
    public class ClaudeCodeClient : ILlm
    {
        readonly string exe;
        public ClaudeCodeClient(string exe) { this.exe = exe; }

        /// <summary>Busca un Claude Code real (descarta shims que solo se llaman "claude").</summary>
        public static string FindExe()
        {
            var cands = new List<string>();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var cc = Path.Combine(appData, "Claude", "claude-code");
            if (Directory.Exists(cc))
                foreach (var d in Directory.GetDirectories(cc).OrderByDescending(x => VersionOf(Path.GetFileName(x))))
                    cands.Add(Path.Combine(d, "claude.exe"));
            cands.Add(Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"));
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                try { if (p.Trim().Length > 0) cands.Add(Path.Combine(p.Trim(), "claude.exe")); } catch { }

            foreach (var c in cands.Distinct())
                if (File.Exists(c) && (Run(c, "--version", null, 15000).Item2 ?? "").Contains("Claude Code")) return c;
            return null;
        }

        static Version VersionOf(string s) { Version v; return Version.TryParse(s, out v) ? v : new Version(0, 0); }

        public static string VersionText(string exe) { return (Run(exe, "--version", null, 15000).Item2 ?? "").Trim(); }

        /// <summary>Abre una terminal con Claude Code para que inicies sesion con tu cuenta (paso unico).</summary>
        public static void OpenLoginTerminal(string exe)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/k \"\"" + exe + "\"\"") { UseShellExecute = true });
        }

        static Tuple<int, string> Run(string exe, string args, byte[] stdin, int timeoutMs, string workDir = null)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = workDir ?? Path.GetTempPath()
            };
            // Si hubiera una API key en el entorno, Claude Code la usaria (y cobraria). Se quita para usar la suscripcion.
            foreach (var v in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN" }) psi.EnvironmentVariables.Remove(v);
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
                if (stdin != null) { p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length); }
                p.StandardInput.Close();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new TimeoutException("Claude Code no respondio a tiempo."); }
                var o = outTask.Result; var e = errTask.Result;
                return Tuple.Create(p.ExitCode, string.IsNullOrWhiteSpace(o) ? e : o + (string.IsNullOrWhiteSpace(e) ? "" : "\n" + e));
            }
        }

        public string Send(string system, IList<ClaudeClient.Turn> turns, int maxTokens = 8192)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "EtlStudio-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== INSTRUCCIONES ===").AppendLine(system).AppendLine();
                bool binaries = false; int n = 0;
                if (turns.Count > 1) sb.AppendLine("=== CONVERSACION ===");
                foreach (var t in turns)
                {
                    foreach (var a in t.Attachments)
                    {
                        if (a.Kind == "text") sb.AppendLine("=== Archivo: " + a.Name + " ===").AppendLine(a.Text).AppendLine();
                        else
                        {
                            var f = Path.Combine(tmp, (++n) + "_" + Regex.Replace(a.Name, @"[^\w\.\-]", "_")); File.WriteAllBytes(f, a.Data); binaries = true;
                            sb.AppendLine("=== Archivo adjunto (" + a.Kind + "): leelo con la herramienta Read en: " + f + " ===").AppendLine();
                        }
                    }
                    if (turns.Count > 1) sb.AppendLine("[" + (t.Role == "user" ? "Usuario" : "Asistente") + "]: " + t.Text).AppendLine();
                    else sb.AppendLine("=== MENSAJE DEL USUARIO ===").AppendLine(t.Text);
                }
                if (turns.Count > 1) sb.AppendLine("Responde SOLO al ultimo mensaje del usuario.");

                string args = "-p --output-format text --no-session-persistence --system-prompt \"Sigue al pie de la letra las instrucciones del mensaje.\" " +
                              (binaries ? "--tools Read --allowedTools Read --add-dir \"" + tmp + "\"" : "--tools \"\"");
                var r = Run(exe, args, new UTF8Encoding(false).GetBytes(sb.ToString()), 300000, tmp);
                var text = (r.Item2 ?? "").Trim();
                if (r.Item1 != 0 || text.Length == 0)
                {
                    if (Regex.IsMatch(text, @"authenticat|OAuth|log ?in|expired|credential", RegexOptions.IgnoreCase))
                        throw new ClaudeLoginRequiredException("Tu sesion de Claude Code no esta iniciada o expiro. En Ajustes pulsa \"Iniciar sesion de Claude\", inicia sesion en la ventana que se abre y vuelve a intentar.\n\n(" + text + ")");
                    throw new Exception("Claude Code fallo (codigo " + r.Item1 + "): " + text);
                }
                return text;
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
