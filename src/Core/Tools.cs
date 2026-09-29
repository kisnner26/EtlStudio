using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace EtlStudio.Core
{
    /// <summary>Ajustes del usuario. La API key se guarda cifrada con DPAPI (solo tu usuario de Windows puede leerla).</summary>
    public class AppSettings
    {
        public string SqlServer { get; set; } = "localhost";
        public string Dsn { get; set; } = "";
        public string Model { get; set; } = "claude-sonnet-5";
        public string Provider { get; set; } = "cli";   // "cli" = Claude Code con tu suscripcion (sin API key) | "api" = API de Anthropic (de pago)
        public string WorkspaceId { get; set; } = "";   // opcional: solo si tu API key no esta asociada a un workspace
        public string ApiKeyProtected { get; set; } = "";

        static string PathFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EtlStudio", "settings.json"); } }

        public static AppSettings Load()
        {
            try { if (File.Exists(PathFile)) return new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(PathFile)); } catch { }
            return new AppSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile));
            File.WriteAllText(PathFile, new JavaScriptSerializer().Serialize(this));
        }

        public string GetApiKey()
        {
            var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            if (string.IsNullOrEmpty(ApiKeyProtected)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ApiKeyProtected), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        public void SetApiKey(string key)
        {
            ApiKeyProtected = string.IsNullOrEmpty(key) ? "" :
                Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }

    public static class SourceReader
    {
        /// <summary>DSN ODBC de usuario y de sistema (64 bits).</summary>
        public static List<string> ListDsns()
        {
            var r = new SortedSet<string>();
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                using (var k = hive.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources"))
                    if (k != null) foreach (var n in k.GetValueNames()) r.Add(n);
            return r.ToList();
        }

        static string Conn(string dsn) { return "DSN=" + dsn + ";"; }

        public static string Test(string dsn)
        {
            using (var c = new OdbcConnection(Conn(dsn))) { c.Open(); return "Conexion ODBC correcta (" + c.Driver + ")."; }
        }

        /// <summary>Ejecuta el SELECT y devuelve maximo 'top' filas para vista previa y esquema.</summary>
        public static DataTable Preview(string dsn, string query, int top, string kind = "Databricks")
        {
            using (var c = new OdbcConnection(Conn(dsn)))
            {
                c.Open();
                var cmd = new OdbcCommand(DwSpec.PreviewSql(kind, query, top), c) { CommandTimeout = 120 };
                var dt = new DataTable();
                using (var rd = cmd.ExecuteReader()) dt.Load(rd);
                return dt;
            }
        }

        /// <summary>Ejecuta una consulta completa (sin LIMIT) contra el DSN de origen; pensada para agregados.</summary>
        public static DataTable Query(string dsn, string sql)
        {
            using (var c = new OdbcConnection(Conn(dsn)))
            {
                c.Open();
                var dt = new DataTable();
                using (var rd = new OdbcCommand(sql, c) { CommandTimeout = 600 }.ExecuteReader()) dt.Load(rd);
                return dt;
            }
        }

        public static string DescribeSchema(DataTable dt)
        {
            var sb = new StringBuilder();
            foreach (DataColumn c in dt.Columns) sb.AppendLine(c.ColumnName + " : " + c.DataType.Name);
            return sb.ToString();
        }

        public static string SampleRows(DataTable dt, int n)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(" | ", dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName)));
            foreach (DataRow r in dt.Rows.Cast<DataRow>().Take(n)) sb.AppendLine(string.Join(" | ", r.ItemArray.Select(v => Convert.ToString(v))));
            return sb.ToString();
        }
    }

    public static class SqlRunner
    {
        public static string ConnStr(string server, string db)
        {
            return "Server=" + server + ";Database=" + db + ";Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15";
        }

        public static string Test(string server)
        {
            using (var c = new SqlConnection(ConnStr(server, "master"))) { c.Open(); return "Conexion a SQL Server correcta (v" + c.ServerVersion + ")."; }
        }

        public static void Exec(string server, string db, string sql)
        {
            using (var c = new SqlConnection(ConnStr(server, db)))
            {
                c.Open();
                // separa por lineas "GO"
                foreach (var batch in System.Text.RegularExpressions.Regex.Split(sql, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    new SqlCommand(batch, c) { CommandTimeout = 0 }.ExecuteNonQuery();
                }
            }
        }

        public static DataTable Query(string server, string db, string sql)
        {
            using (var c = new SqlConnection(ConnStr(server, db)))
            {
                var dt = new DataTable();
                new SqlDataAdapter(new SqlCommand(sql, c) { CommandTimeout = 300 }).Fill(dt);
                return dt;
            }
        }
    }

    public static class DtExec
    {
        public static string Find()
        {
            var root = @"C:\Program Files\Microsoft SQL Server";
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root).Select(d => Path.Combine(d, @"DTS\Binn\DTExec.exe")).Where(File.Exists)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        /// <summary>Ejecuta dtexec y emite cada linea por 'onLine'. Devuelve el codigo de salida.</summary>
        public static int Run(string package, bool validateOnly, Action<string> onLine)
        {
            var exe = Find();
            if (exe == null) throw new FileNotFoundException("No se encontro DTExec.exe. Instala SQL Server Integration Services (2022) o el runtime de SSIS.");
            var psi = new ProcessStartInfo(exe, "/F \"" + package + "\" /Rep EW" + (validateOnly ? " /Validate" : ""))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); };
            p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); p.WaitForExit();
            return p.ExitCode;
        }
    }
}
