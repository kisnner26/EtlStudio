using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace EtlStudio.Core
{
    public class Requirement { public string Name, Detail, Fix; public bool Ok; }

    /// <summary>Comprueba lo que EtlStudio necesita en este equipo y, si falta algo, dice como resolverlo.</summary>
    public static class Requirements
    {
        static bool Reg(string path, string value, out object v)
        {
            v = null;
            try { using (var k = Registry.LocalMachine.OpenSubKey(path)) { if (k == null) return false; v = value == null ? "" : k.GetValue(value); return v != null; } }
            catch { return false; }
        }

        public static List<Requirement> Check(string sourceDsn = null)
        {
            var l = new List<Requirement>();
            object v;

            bool net = Reg(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", out v) && Convert.ToInt32(v) >= 461808;   // 4.7.2
            l.Add(new Requirement { Name = ".NET Framework 4.7.2 o superior", Ok = net, Detail = net ? "instalado" : "no encontrado", Fix = "Instala .NET Framework 4.8 desde https://dotnet.microsoft.com/download/dotnet-framework" });

            var dtexec = DtExec.Find();
            l.Add(new Requirement { Name = "SSIS 2022: DTExec.exe", Ok = dtexec != null, Detail = dtexec ?? "no encontrado", Fix = "Instala SQL Server Integration Services 2022 (o la extension SSIS de Visual Studio) y reinicia la sesion." });

            var gac = Path.Combine(Environment.GetEnvironmentVariable("windir") ?? @"C:\Windows", @"Microsoft.NET\assembly\GAC_MSIL\Microsoft.SqlServer.ManagedDTS");
            bool dts = Directory.Exists(gac) && Directory.GetDirectories(gac).Any(d => Path.GetFileName(d).Contains("16.0.0.0"));
            l.Add(new Requirement { Name = "Ensamblados de SSIS v16 (ManagedDTS)", Ok = dts, Detail = dts ? "en el GAC" : "no encontrados", Fix = "Vienen con SQL Server Integration Services 2022. Sin ellos no se puede generar el paquete." });

            bool oledb = Reg(@"SOFTWARE\Microsoft\MSOLEDBSQL", null, out v) || Reg(@"SOFTWARE\Microsoft\MSOLEDBSQL19", null, out v);
            l.Add(new Requirement { Name = "OLE DB Driver for SQL Server (MSOLEDBSQL)", Ok = oledb, Detail = oledb ? "instalado" : "no encontrado", Fix = "Descargalo de https://learn.microsoft.com/sql/connect/oledb/download-oledb-driver-for-sql-server" });

            bool ace = Reg(@"SOFTWARE\Classes\Microsoft.ACE.OLEDB.16.0", null, out v);
            l.Add(new Requirement { Name = "Microsoft.ACE.OLEDB.16.0 (solo para metas en Excel)", Ok = ace, Detail = ace ? "instalado" : "no encontrado", Fix = "Instala Microsoft Access Database Engine 2016 Redistributable (64 bits). Solo hace falta si el diseno tiene metas de Excel." });

            List<string> dsns; try { dsns = SourceReader.ListDsns(); } catch { dsns = new List<string>(); }
            if (!string.IsNullOrWhiteSpace(sourceDsn))
            {
                bool has = dsns.Contains(sourceDsn, StringComparer.OrdinalIgnoreCase);
                l.Add(new Requirement { Name = "DSN ODBC '" + sourceDsn + "'", Ok = has, Detail = has ? "existe" : "no existe", Fix = "Creelo en el administrador ODBC de 64 bits (DSN de usuario) y pega tu token en Password; EtlStudio puede crearlo si el enunciado trae host y HTTP path." });
            }
            else l.Add(new Requirement { Name = "DSN ODBC", Ok = dsns.Count > 0, Detail = dsns.Count + " encontrados", Fix = "Instala el driver ODBC del origen (p. ej. Databricks/Simba) y crea un DSN." });
            return l;
        }

        public static string Format(List<Requirement> rs)
        {
            return string.Join("\n", rs.Select(r => (r.Ok ? "  [OK]  " : "  [FALTA] ") + r.Name + " - " + r.Detail + (r.Ok ? "" : "\n           -> " + r.Fix)));
        }
    }
}
