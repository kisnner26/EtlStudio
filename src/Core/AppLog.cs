using System;
using System.IO;
using System.Text;

namespace EtlStudio.Core
{
    /// <summary>Registro de la aplicacion en %APPDATA%\EtlStudio\logs\etlstudio.log, con rotacion por tamano. Nunca lanza excepciones.</summary>
    public static class AppLog
    {
        public const long MaxBytes = 1024 * 1024;
        public const int Keep = 5;
        static readonly object gate = new object();
        static string dir;

        public static string LogDirectory
        {
            get { return dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EtlStudio", "logs"); }
            set { dir = value; }
        }

        public static string FilePath { get { return Path.Combine(LogDirectory, "etlstudio.log"); } }

        public static void Info(string msg) { Write("INFO ", msg); }

        public static void Error(string msg, Exception ex = null) { Write("ERROR", ex == null ? msg : msg + ": " + ex); }

        static void Write(string level, string msg)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    Rotate();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + Scrub(msg) + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        /// <summary>Quita secretos evidentes (tokens, contrasenas) antes de escribir en disco.</summary>
        public static string Scrub(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?i)\b(pwd|password|token|api[_-]?key)\s*[=:]\s*[^;\s""']+", "$1=***");
            return System.Text.RegularExpressions.Regex.Replace(s, @"\b(dapi|sk-ant-)[A-Za-z0-9_\-]{8,}", "***");
        }

        static void Rotate()
        {
            var f = new FileInfo(FilePath);
            if (!f.Exists || f.Length < MaxBytes) return;
            for (int i = Keep - 1; i >= 1; i--)
            {
                var from = FilePath + "." + i; var to = FilePath + "." + (i + 1);
                if (File.Exists(from)) { if (File.Exists(to)) File.Delete(to); File.Move(from, to); }
            }
            File.Move(FilePath, FilePath + ".1");
        }
    }
}
