using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace EtlStudio.Core
{
    public class ConexionInfo
    {
        public string Host { get; set; }
        public string HttpPath { get; set; }
        public string DsnSugerido { get; set; }
    }

    /// <summary>Lo que Claude entiende del enunciado: que construir y con que datos. El usuario lo aprueba antes de ejecutar.</summary>
    public class Plan
    {
        public string Titulo { get; set; }
        public string Resumen { get; set; }
        public ConexionInfo Conexion { get; set; } = new ConexionInfo();
        public DwSpec Spec { get; set; }
        public List<string> Pasos { get; set; } = new List<string>();
        public List<string> Advertencias { get; set; } = new List<string>();

        public static Plan FromJson(string json)
        {
            var p = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Plan>(json);
            if (p == null || p.Spec == null) throw new Exception("El plan no trae el diseno del DW (Spec).");
            if (p.Conexion == null) p.Conexion = new ConexionInfo();
            if (p.Pasos == null) p.Pasos = new List<string>();
            if (p.Advertencias == null) p.Advertencias = new List<string>();
            return p;
        }
    }

    /// <summary>Instancias locales de SQL Server y creacion del DSN ODBC de Databricks (sin token: ese lo pones tu).</summary>
    public static class Environments
    {
        public static List<string> LocalSqlInstances()
        {
            var r = new List<string>();
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
                if (k != null)
                    foreach (var n in k.GetValueNames())
                        r.Add(n.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "localhost" : "localhost\\" + n);
            return r;
        }

        public static string FindDatabricksDriver()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers"))
                if (k != null)
                    foreach (var n in k.GetValueNames())
                        if (n.IndexOf("Databricks", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Simba Spark", StringComparison.OrdinalIgnoreCase) >= 0) return n;
            return null;
        }

        public static bool DsnExists(string name)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                using (var k = hive.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources"))
                    if (k != null && k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Crea un DSN de usuario (no requiere administrador). NO escribe el token: se pega en el dialogo del driver.</summary>
        public static void CreateUserDsn(string name, string host, string httpPath)
        {
            var driver = FindDatabricksDriver();
            if (driver == null) throw new Exception("No esta instalado el Databricks ODBC Driver (descargalo de databricks.com/spark/odbc-drivers-download).");
            string driverPath;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\" + driver)) driverPath = Convert.ToString(k.GetValue("Driver"));
            using (var ds = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources")) ds.SetValue(name, driver);
            using (var k = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\ODBC\ODBC.INI\" + name))
            {
                var v = new Dictionary<string, string>
                {
                    { "Driver", driverPath }, { "Description", "Creado por EtlStudio" }, { "Host", host }, { "Port", "443" }, { "Schema", "default" },
                    { "HTTPPath", httpPath }, { "AuthMech", "3" }, { "UID", "token" }, { "ThriftTransport", "2" }, { "SSL", "1" }, { "SparkServerType", "3" }
                };
                foreach (var kv in v) k.SetValue(kv.Key, kv.Value);
            }
        }
    }

    /// <summary>Se lanza cuando el DSN existe pero le falta el token (paso manual, una sola vez).</summary>
    public class NeedsTokenException : Exception { public NeedsTokenException(string m) : base(m) { } }
}
