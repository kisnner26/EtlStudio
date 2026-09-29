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
                if (d.HasNaturalKey) sb.AppendLine("    CONSTRAINT UQ_" + d.Name + "_LlaveNatural UNIQUE (" + string.Join(", ", d.LookupColumns().Select(c => c.Dst)) + "),");
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

            sb.AppendLine("-- Auditoria de ejecuciones");
            sb.AppendLine(AuditDdl());
            sb.AppendLine();

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

        /// <summary>Tabla de auditoria de ejecuciones (idempotente). Sobrevive a la limpieza del destino.</summary>
        public static string AuditDdl()
        {
            return "IF OBJECT_ID('dbo." + DwSpec.AuditTableName + "') IS NULL CREATE TABLE dbo." + DwSpec.AuditTableName + " (\n" +
                   "    Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, Paquete VARCHAR(128) NOT NULL,\n" +
                   "    Inicio DATETIME NOT NULL DEFAULT GETDATE(), Fin DATETIME NULL, Estado VARCHAR(20) NOT NULL,\n" +
                   "    Filas_Fact BIGINT NULL, Filas_Dimensiones BIGINT NULL);";
        }

        /// <summary>Primer paso del paquete: cierra como 'Fallido' las corridas que quedaron abiertas y abre una nueva.</summary>
        public static string AuditStart(DwSpec s)
        {
            return AuditDdl() + "\n" +
                   "UPDATE dbo." + DwSpec.AuditTableName + " SET Fin = GETDATE(), Estado = 'Fallido' WHERE Estado = 'EnCurso';\n" +
                   "INSERT INTO dbo." + DwSpec.AuditTableName + " (Paquete, Estado) VALUES (N'" + s.DwName + "', 'EnCurso');";
        }

        /// <summary>Ultimo paso: solo se alcanza si todo el flujo anterior termino bien.</summary>
        public static string AuditEnd(DwSpec s)
        {
            var dims = string.Join(" + ", s.Dims.Select(d => "(SELECT COUNT_BIG(*) FROM dbo." + d.Name + ")"));
            return "UPDATE dbo." + DwSpec.AuditTableName + " SET Fin = GETDATE(), Estado = 'OK',\n" +
                   "    Filas_Fact = (SELECT COUNT_BIG(*) FROM dbo." + s.Fact.Name + "),\n" +
                   "    Filas_Dimensiones = " + dims + "\n" +
                   "WHERE Id = (SELECT MAX(Id) FROM dbo." + DwSpec.AuditTableName + " WHERE Estado = 'EnCurso');";
        }

        public const string StagingSchema = "stg";

        /// <summary>Tablas de paso (esquema stg): la carga escribe aqui y el destino solo cambia al publicar. Se recrean en cada corrida.</summary>
        public static string StagingPrepare(DwSpec s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("IF SCHEMA_ID('" + StagingSchema + "') IS NULL EXEC('CREATE SCHEMA " + StagingSchema + "');");
            foreach (var n in StagingTables(s).AsEnumerable().Reverse()) sb.AppendLine("IF OBJECT_ID('" + StagingSchema + "." + n + "', 'U') IS NOT NULL DROP TABLE " + StagingSchema + "." + n + ";");
            foreach (var d in s.Dims)
            {
                sb.AppendLine("CREATE TABLE " + StagingSchema + "." + d.Name + " (" + d.Key + " INT IDENTITY(1,1) NOT NULL PRIMARY KEY, "
                    + string.Join(", ", d.Columns.Select(c => c.Dst + " " + c.Type.ToUpper() + " NOT NULL"))
                    + (d.HasNaturalKey ? ", CONSTRAINT UQ_stg_" + d.Name + " UNIQUE (" + string.Join(", ", d.LookupColumns().Select(c => c.Dst)) + ")" : "") + ");");
            }
            var f = s.Fact;
            sb.AppendLine("CREATE TABLE " + StagingSchema + "." + f.Name + " (SK_Tiempo INT NOT NULL, " + string.Concat(s.Dims.Select(d => d.Key + " INT NOT NULL, "))
                + f.BusinessKey.Dst + " " + f.BusinessKey.Type.ToUpper() + " NOT NULL, " + string.Join(", ", f.Measures.Select(m => m.Dst + " " + m.Type.ToUpper() + " NOT NULL")) + ");");
            if (s.Goals != null)
            {
                var gd = s.Dims.First(d => d.Name == s.Goals.DimName);
                sb.AppendLine("CREATE TABLE " + StagingSchema + "." + s.Goals.Table + " (SK_Tiempo INT NOT NULL, " + gd.Key + " INT NOT NULL, Monto_Meta DECIMAL(18,2) NOT NULL);");
            }
            return sb.ToString();
        }

        /// <summary>Tablas de paso en orden de creacion (dimensiones, hechos, metas).</summary>
        static System.Collections.Generic.List<string> StagingTables(DwSpec s)
        {
            var l = s.Dims.Select(d => d.Name).ToList(); l.Add(s.Fact.Name);
            if (s.Goals != null) l.Add(s.Goals.Table);
            return l;
        }

        /// <summary>
        /// Publica lo cargado en stg hacia dbo en una sola transaccion: si algo falla, se deshace y el destino queda como estaba.
        /// Las dimensiones conservan sus llaves (IDENTITY_INSERT), asi los hechos ya apuntan a ellas.
        /// </summary>
        public static string Publish(DwSpec s)
        {
            var sb = new StringBuilder();
            var f = s.Fact;
            sb.AppendLine("SET XACT_ABORT ON;");
            sb.AppendLine("BEGIN TRY");
            sb.AppendLine("BEGIN TRANSACTION;");
            if (s.Goals != null) sb.AppendLine("DELETE FROM dbo." + s.Goals.Table + ";");
            sb.AppendLine("DELETE FROM dbo." + f.Name + ";");
            foreach (var d in s.Dims) sb.AppendLine("DELETE FROM dbo." + d.Name + ";");
            foreach (var n in new[] { f.Name }.Concat(s.Goals != null ? new[] { s.Goals.Table } : new string[0]))
                sb.AppendLine("IF EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID('dbo." + n + "') AND last_value IS NOT NULL) DBCC CHECKIDENT ('dbo." + n + "', RESEED, 0) WITH NO_INFOMSGS;");
            foreach (var d in s.Dims)
            {
                var cols = d.Key + ", " + string.Join(", ", d.Columns.Select(c => c.Dst));
                sb.AppendLine("SET IDENTITY_INSERT dbo." + d.Name + " ON;");
                sb.AppendLine("INSERT INTO dbo." + d.Name + " (" + cols + ") SELECT " + cols + " FROM " + StagingSchema + "." + d.Name + ";");
                sb.AppendLine("SET IDENTITY_INSERT dbo." + d.Name + " OFF;");
            }
            var fcols = "SK_Tiempo, " + string.Concat(s.Dims.Select(d => d.Key + ", ")) + f.BusinessKey.Dst + ", " + string.Join(", ", f.Measures.Select(m => m.Dst));
            sb.AppendLine("INSERT INTO dbo." + f.Name + " (" + fcols + ") SELECT " + fcols + " FROM " + StagingSchema + "." + f.Name + ";");
            if (s.Goals != null)
            {
                var gd = s.Dims.First(d => d.Name == s.Goals.DimName);
                sb.AppendLine("INSERT INTO dbo." + s.Goals.Table + " (SK_Tiempo, " + gd.Key + ", Monto_Meta) SELECT SK_Tiempo, " + gd.Key + ", Monto_Meta FROM " + StagingSchema + "." + s.Goals.Table + ";");
            }
            sb.AppendLine("COMMIT TRANSACTION;");
            sb.AppendLine("END TRY");
            sb.AppendLine("BEGIN CATCH");
            sb.AppendLine("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            sb.AppendLine("THROW;");
            sb.AppendLine("END CATCH;");
            foreach (var n in StagingTables(s).AsEnumerable().Reverse()) sb.AppendLine("DROP TABLE " + StagingSchema + "." + n + ";");
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
