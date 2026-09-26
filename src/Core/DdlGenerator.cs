using System.Linq;
using System.Text;

namespace EtlStudio.Core
{
    /// <summary>Genera el T-SQL del DW en estrella a partir del DwSpec (idempotente salvo "recrear").</summary>
    public static class DdlGenerator
    {
        public static string DropDatabase(DwSpec s)
        {
            return "IF DB_ID(N'" + s.DwName + "') IS NOT NULL\nBEGIN\n    ALTER DATABASE [" + s.DwName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\n    DROP DATABASE [" + s.DwName + "];\nEND";
        }

        public static string CreateDatabase(DwSpec s)
        {
            return "IF DB_ID(N'" + s.DwName + "') IS NULL CREATE DATABASE [" + s.DwName + "];";
        }

        /// <summary>Script de tablas (ejecutar dentro de la base del DW).</summary>
        public static string Tables(DwSpec s)
        {
            var sb = new StringBuilder();
            var t = s.Time;

            sb.AppendLine("-- Dimension tiempo");
            sb.AppendLine("IF OBJECT_ID('dbo." + t.Name + "') IS NULL CREATE TABLE dbo." + t.Name + " (");
            sb.AppendLine("    SK_Tiempo INT NOT NULL PRIMARY KEY, Fecha DATE NOT NULL, Anio INT NOT NULL, Trimestre INT NOT NULL,");
            sb.AppendLine("    Nombre_Trimestre VARCHAR(10) NOT NULL, Mes INT NOT NULL, Nombre_Mes VARCHAR(20) NOT NULL, Dia INT NOT NULL,");
            sb.AppendLine("    Dia_Semana INT NOT NULL, Nombre_Dia_Semana VARCHAR(20) NOT NULL, Es_Fin_De_Semana BIT NOT NULL);");
            sb.AppendLine();

            foreach (var d in s.Dims)
            {
                sb.AppendLine("-- Dimension " + d.Name);
                sb.AppendLine("IF OBJECT_ID('dbo." + d.Name + "') IS NULL CREATE TABLE dbo." + d.Name + " (");
                sb.AppendLine("    " + d.Key + " INT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
                foreach (var c in d.Columns) sb.AppendLine("    " + c.Dst + " " + c.Type.ToUpper() + " NOT NULL,");
                sb.AppendLine("    Fecha_Creacion DATETIME DEFAULT GETDATE());");
                sb.AppendLine();
            }

            var f = s.Fact;
            sb.AppendLine("-- Tabla de hechos " + f.Name);
            sb.AppendLine("IF OBJECT_ID('dbo." + f.Name + "') IS NULL CREATE TABLE dbo." + f.Name + " (");
            sb.AppendLine("    SK_Fact BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
            sb.AppendLine("    SK_Tiempo INT NOT NULL,");
            foreach (var d in s.Dims) sb.AppendLine("    " + d.Key + " INT NOT NULL,");
            sb.AppendLine("    " + f.BusinessKey.Dst + " " + f.BusinessKey.Type.ToUpper() + " NOT NULL,");
            foreach (var m in f.Measures) sb.AppendLine("    " + m.Dst + " " + m.Type.ToUpper() + " NOT NULL,");
            foreach (var c in f.Computed) sb.AppendLine("    " + c.Name + " AS " + c.Expr + " PERSISTED,");
            sb.AppendLine("    Fecha_Carga DATETIME DEFAULT GETDATE(),");
            sb.AppendLine("    CONSTRAINT FK_" + f.Name + "_Tiempo FOREIGN KEY (SK_Tiempo) REFERENCES dbo." + t.Name + "(SK_Tiempo)"
                + string.Concat(s.Dims.Select(d => ",\n    CONSTRAINT FK_" + f.Name + "_" + d.Name + " FOREIGN KEY (" + d.Key + ") REFERENCES dbo." + d.Name + "(" + d.Key + ")")) + ");");
            sb.AppendLine();

            if (s.Goals != null)
            {
                var g = s.Goals; var gd = s.Dims.First(d => d.Name == g.DimName);
                sb.AppendLine("-- Metas (Excel)");
                sb.AppendLine("IF OBJECT_ID('dbo." + g.Table + "') IS NULL CREATE TABLE dbo." + g.Table + " (");
                sb.AppendLine("    SK_Meta INT IDENTITY(1,1) NOT NULL PRIMARY KEY, SK_Tiempo INT NOT NULL, " + gd.Key + " INT NOT NULL,");
                sb.AppendLine("    Monto_Meta DECIMAL(18,2) NOT NULL, Fecha_Carga DATETIME DEFAULT GETDATE(),");
                sb.AppendLine("    CONSTRAINT FK_" + g.Table + "_Tiempo FOREIGN KEY (SK_Tiempo) REFERENCES dbo." + t.Name + "(SK_Tiempo),");
                sb.AppendLine("    CONSTRAINT FK_" + g.Table + "_" + gd.Name + " FOREIGN KEY (" + gd.Key + ") REFERENCES dbo." + gd.Name + "(" + gd.Key + "));");
                sb.AppendLine();
            }

            sb.AppendLine("-- Poblar dimension tiempo (set-based, idempotente)");
            sb.AppendLine("DECLARE @ini DATE = '" + t.From + "', @fin DATE = '" + t.To + "';");
            sb.AppendLine(";WITH n AS (SELECT TOP (DATEDIFF(DAY,@ini,@fin)+1) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1 AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)");
            sb.AppendLine("INSERT INTO dbo." + t.Name + " (SK_Tiempo, Fecha, Anio, Trimestre, Nombre_Trimestre, Mes, Nombre_Mes, Dia, Dia_Semana, Nombre_Dia_Semana, Es_Fin_De_Semana)");
            sb.AppendLine("SELECT CAST(CONVERT(VARCHAR(8), d, 112) AS INT), d, YEAR(d), DATEPART(QUARTER, d), 'Q' + CAST(DATEPART(QUARTER, d) AS VARCHAR(1)),");
            sb.AppendLine("       MONTH(d), DATENAME(MONTH, d), DAY(d), DATEPART(WEEKDAY, d), DATENAME(WEEKDAY, d), CASE WHEN DATEPART(WEEKDAY, d) IN (1, 7) THEN 1 ELSE 0 END");
            sb.AppendLine("FROM (SELECT DATEADD(DAY, i, @ini) AS d FROM n) x");
            sb.AppendLine("WHERE NOT EXISTS (SELECT 1 FROM dbo." + t.Name + " t WHERE t.SK_Tiempo = CAST(CONVERT(VARCHAR(8), x.d, 112) AS INT));");
            return sb.ToString();
        }

        /// <summary>Sentencias de limpieza previas a cada carga (orden respetando FKs, reseed condicional).</summary>
        public static string Cleanup(DwSpec s)
        {
            var sb = new StringBuilder();
            var order = new System.Collections.Generic.List<string>();
            if (s.Goals != null) order.Add(s.Goals.Table);
            order.Add(s.Fact.Name);
            foreach (var d in s.Dims) order.Add(d.Name);
            foreach (var n in order) sb.AppendLine("DELETE FROM dbo." + n + ";");
            foreach (var n in order)
                sb.AppendLine("IF EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID('dbo." + n + "') AND last_value IS NOT NULL) DBCC CHECKIDENT ('dbo." + n + "', RESEED, 0);");
            return sb.ToString();
        }
    }
}
