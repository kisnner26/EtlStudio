using System;
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
                if (d.Computed != null) foreach (var c in d.Computed) sb.AppendLine("    " + c.Name + " AS " + c.Expr + " PERSISTED,");
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

            if (s.RejectsRows)
            {
                sb.AppendLine("-- Filas rechazadas (sin dimension o con fecha fuera de la dimension tiempo)");
                sb.AppendLine(RejectsDdl(s));
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

        /// <summary>Columna de la tabla de paso "cruda" de la Fact (modo Reject): una por columna de origen, con prefijo s_.</summary>
        public class RawColumn { public string Name, Type; public ColumnMap Map; public bool IsMeasure; }

        /// <summary>Columnas de origen que necesita la Fact en modo Reject, sin repetir: dimensiones, llave de negocio y medidas.</summary>
        public static System.Collections.Generic.List<RawColumn> RawColumns(DwSpec s)
        {
            var l = new System.Collections.Generic.List<RawColumn>(); var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Action<ColumnMap, bool> add = (c, m) => { if (seen.Add(c.Src)) l.Add(new RawColumn { Name = "s_" + c.Src, Type = c.Type.ToUpper(), Map = c, IsMeasure = m }); };
            foreach (var d in s.Dims) foreach (var c in d.Columns) add(c, false);
            add(s.Fact.BusinessKey, false);
            foreach (var m in s.Fact.Measures) add(m, true);
            return l;
        }

        static string RawDdl(DwSpec s) { return string.Join(", ", RawColumns(s).Select(c => c.Name + " " + c.Type + " NOT NULL")); }

        /// <summary>Tabla dbo.ETL_Rechazos: las filas que no pudieron cargarse, con el motivo y sus valores.</summary>
        public static string RejectsDdl(DwSpec s)
        {
            return "IF OBJECT_ID('dbo." + DwSpec.RejectTableName + "') IS NULL CREATE TABLE dbo." + DwSpec.RejectTableName + " (\n" +
                   "    Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, Motivo VARCHAR(200) NOT NULL, SK_Tiempo INT NULL, " + RawDdl(s) + ",\n" +
                   "    Fecha_Rechazo DATETIME NOT NULL DEFAULT GETDATE());";
        }

        /// <summary>
        /// Tablas de paso (esquema stg): la carga escribe aqui y el destino solo cambia al publicar. Se recrean en cada corrida.
        /// Las dimensiones tienen una tabla de aterrizaje (_Nuevos) y la tabla stg definitiva; en cargas incrementales esta ultima
        /// arranca con los miembros ya existentes (con sus llaves) para que los Lookups vean tambien lo cargado antes.
        /// </summary>
        public static string StagingPrepare(DwSpec s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("IF SCHEMA_ID('" + StagingSchema + "') IS NULL EXEC('CREATE SCHEMA " + StagingSchema + "');");
            foreach (var n in StagingTables(s).AsEnumerable().Reverse()) sb.AppendLine("IF OBJECT_ID('" + StagingSchema + "." + n + "', 'U') IS NOT NULL DROP TABLE " + StagingSchema + "." + n + ";");
            foreach (var d in s.Dims)
            {
                var cols = string.Join(", ", d.Columns.Select(c => c.Dst + " " + c.Type.ToUpper() + " NOT NULL"));
                sb.AppendLine("CREATE TABLE " + StagingSchema + "." + d.Name + "_Nuevos (" + cols + ");");
                sb.AppendLine("CREATE TABLE " + StagingSchema + "." + d.Name + " (" + d.Key + " INT IDENTITY(1,1) NOT NULL PRIMARY KEY, " + cols
                    + (d.HasNaturalKey ? ", CONSTRAINT UQ_stg_" + d.Name + " UNIQUE (" + string.Join(", ", d.LookupColumns().Select(c => c.Dst)) + ")" : "") + ");");
                if (s.IsIncremental)
                {
                    var all = d.Key + ", " + string.Join(", ", d.Columns.Select(c => c.Dst));
                    sb.AppendLine("SET IDENTITY_INSERT " + StagingSchema + "." + d.Name + " ON;");
                    sb.AppendLine("INSERT INTO " + StagingSchema + "." + d.Name + " (" + all + ") SELECT " + all + " FROM dbo." + d.Name + ";");
                    sb.AppendLine("SET IDENTITY_INSERT " + StagingSchema + "." + d.Name + " OFF;");
                }
            }
            var f = s.Fact;
            if (s.RejectsRows)
                sb.AppendLine("CREATE TABLE " + StagingSchema + "." + f.Name + " (SK_Tiempo INT NOT NULL, " + RawDdl(s) + ");");
            else
                sb.AppendLine("CREATE TABLE " + StagingSchema + "." + f.Name + " (SK_Tiempo INT NOT NULL, " + string.Concat(s.Dims.Select(d => d.Key + " INT NOT NULL, "))
                    + f.BusinessKey.Dst + " " + f.BusinessKey.Type.ToUpper() + " NOT NULL, " + string.Join(", ", f.Measures.Select(m => m.Dst + " " + m.Type.ToUpper() + " NOT NULL")) + ");");
            if (s.Goals != null)
            {
                var gd = s.Dims.First(d => d.Name == s.Goals.DimName);
                sb.AppendLine("CREATE TABLE " + StagingSchema + "." + s.Goals.Table + " (SK_Tiempo INT NOT NULL, " + gd.Key + " INT NOT NULL, Monto_Meta DECIMAL(18,2) NOT NULL);");
            }
            return sb.ToString();
        }

        /// <summary>Tablas de paso en orden de creacion (aterrizajes y dimensiones, hechos, metas).</summary>
        static System.Collections.Generic.List<string> StagingTables(DwSpec s)
        {
            var l = new System.Collections.Generic.List<string>();
            foreach (var d in s.Dims) { l.Add(d.Name + "_Nuevos"); l.Add(d.Name); }
            l.Add(s.Fact.Name);
            if (s.Goals != null) l.Add(s.Goals.Table);
            return l;
        }

        /// <summary>Paso entre la carga de dimensiones y la de hechos: agrega a stg.Dim_* los miembros nuevos (los que aun no existen por su llave natural).</summary>
        public static string MergeDimensions(DwSpec s)
        {
            var sb = new StringBuilder();
            foreach (var d in s.Dims)
            {
                var cols = string.Join(", ", d.Columns.Select(c => c.Dst));
                sb.AppendLine("INSERT INTO " + StagingSchema + "." + d.Name + " (" + cols + ") SELECT DISTINCT " + string.Join(", ", d.Columns.Select(c => "n." + c.Dst))
                    + " FROM " + StagingSchema + "." + d.Name + "_Nuevos n WHERE NOT EXISTS (SELECT 1 FROM " + StagingSchema + "." + d.Name + " x WHERE "
                    + string.Join(" AND ", d.LookupColumns().Select(c => "x." + c.Dst + " = n." + c.Dst)) + ");");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Publica lo cargado en stg hacia dbo en una sola transaccion: si algo falla, se deshace y el destino queda como estaba.
        /// Las dimensiones conservan sus llaves (IDENTITY_INSERT), asi los hechos ya apuntan a ellas. En modo Full vacia el destino
        /// antes de insertar; en Incremental solo agrega lo nuevo (los hechos ya cargados se reconocen por su llave de negocio).
        /// Con RejectPolicy 'Reject' cruza la Fact cruda contra las dimensiones y manda a ETL_Rechazos lo que no encuentra dimension.
        /// </summary>
        public static string Publish(DwSpec s)
        {
            var sb = new StringBuilder();
            var f = s.Fact; var full = !s.IsIncremental; string stg = StagingSchema + ".";
            sb.AppendLine("SET XACT_ABORT ON;");
            sb.AppendLine("BEGIN TRY");
            sb.AppendLine("BEGIN TRANSACTION;");
            if (s.Goals != null) sb.AppendLine("DELETE FROM dbo." + s.Goals.Table + ";");   // las metas vienen completas del Excel en cualquier modo
            if (full)
            {
                sb.AppendLine("DELETE FROM dbo." + f.Name + ";");
                if (s.RejectsRows) sb.AppendLine("DELETE FROM dbo." + DwSpec.RejectTableName + ";");
                foreach (var d in s.Dims) sb.AppendLine("DELETE FROM dbo." + d.Name + ";");
            }
            var reseed = new System.Collections.Generic.List<string>();
            if (full) reseed.Add(f.Name);
            if (s.Goals != null) reseed.Add(s.Goals.Table);
            if (full && s.RejectsRows) reseed.Add(DwSpec.RejectTableName);
            foreach (var n in reseed)
                sb.AppendLine("IF EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID('dbo." + n + "') AND last_value IS NOT NULL) DBCC CHECKIDENT ('dbo." + n + "', RESEED, 0) WITH NO_INFOMSGS;");
            foreach (var d in s.Dims)
            {
                var cols = d.Key + ", " + string.Join(", ", d.Columns.Select(c => c.Dst));
                sb.AppendLine("SET IDENTITY_INSERT dbo." + d.Name + " ON;");
                sb.AppendLine("INSERT INTO dbo." + d.Name + " (" + cols + ") SELECT " + "s." + d.Key + ", " + string.Join(", ", d.Columns.Select(c => "s." + c.Dst))
                    + " FROM " + stg + d.Name + " s WHERE NOT EXISTS (SELECT 1 FROM dbo." + d.Name + " x WHERE x." + d.Key + " = s." + d.Key + ");");
                sb.AppendLine("SET IDENTITY_INSERT dbo." + d.Name + " OFF;");
            }
            var fcols = "SK_Tiempo, " + string.Concat(s.Dims.Select(d => d.Key + ", ")) + f.BusinessKey.Dst + ", " + string.Join(", ", f.Measures.Select(m => m.Dst));
            string notLoaded = s.IsIncremental ? " AND NOT EXISTS (SELECT 1 FROM dbo." + f.Name + " x WHERE x." + f.BusinessKey.Dst + " = j." + ("s_" + f.BusinessKey.Src) + ")" : "";
            if (!s.RejectsRows)
            {
                sb.AppendLine("INSERT INTO dbo." + f.Name + " (" + fcols + ") SELECT " + fcols + " FROM " + stg + f.Name
                    + (s.IsIncremental ? " j WHERE NOT EXISTS (SELECT 1 FROM dbo." + f.Name + " x WHERE x." + f.BusinessKey.Dst + " = j." + f.BusinessKey.Dst + ")" : "") + ";");
            }
            else
            {
                var raw = RawColumns(s);
                var sel = new System.Collections.Generic.List<string> { "r.*", "t.SK_Tiempo AS ok_t" };
                var joins = new StringBuilder();
                int i = 0;
                foreach (var d in s.Dims)
                {
                    i++;
                    joins.AppendLine("OUTER APPLY (SELECT TOP 1 x." + d.Key + " AS k FROM " + stg + d.Name + " x WHERE "
                        + string.Join(" AND ", d.LookupColumns().Select(c => "x." + c.Dst + " = r.s_" + c.Src)) + " ORDER BY x." + d.Key + ") a" + i);
                    sel.Add("a" + i + ".k AS k_" + i);
                }
                sb.AppendLine("SELECT " + string.Join(", ", sel) + " INTO #j FROM " + stg + f.Name + " r LEFT JOIN dbo." + s.Time.Name + " t ON t.SK_Tiempo = r.SK_Tiempo");
                sb.Append(joins.ToString());
                sb.AppendLine(";");
                var okCond = "j.ok_t IS NOT NULL" + string.Concat(Enumerable.Range(1, s.Dims.Count).Select(k => " AND j.k_" + k + " IS NOT NULL"));
                var ins = "j.SK_Tiempo, " + string.Concat(Enumerable.Range(1, s.Dims.Count).Select(k => "j.k_" + k + ", ")) + "j.s_" + f.BusinessKey.Src + ", " + string.Join(", ", f.Measures.Select(m => "j.s_" + m.Src));
                sb.AppendLine("INSERT INTO dbo." + f.Name + " (" + fcols + ") SELECT " + ins + " FROM #j j WHERE " + okCond + notLoaded + ";");
                var motivo = "CASE WHEN j.ok_t IS NULL THEN 'Fecha fuera de " + s.Time.Name + "'" + string.Concat(s.Dims.Select((d, k) => " WHEN j.k_" + (k + 1) + " IS NULL THEN 'Sin dimension " + d.Name + "'")) + " END";
                var rawNames = string.Join(", ", raw.Select(c => c.Name));
                sb.AppendLine("INSERT INTO dbo." + DwSpec.RejectTableName + " (Motivo, SK_Tiempo, " + rawNames + ") SELECT " + motivo + ", j.SK_Tiempo, " + string.Join(", ", raw.Select(c => "j." + c.Name))
                    + " FROM #j j WHERE NOT (" + okCond + ")"
                    + (s.IsIncremental ? " AND NOT EXISTS (SELECT 1 FROM dbo." + DwSpec.RejectTableName + " q WHERE q.s_" + f.BusinessKey.Src + " = j.s_" + f.BusinessKey.Src + ")" : "") + ";");
                sb.AppendLine("DROP TABLE #j;");
            }
            if (s.Goals != null)
            {
                var gd = s.Dims.First(d => d.Name == s.Goals.DimName);
                sb.AppendLine("INSERT INTO dbo." + s.Goals.Table + " (SK_Tiempo, " + gd.Key + ", Monto_Meta) SELECT SK_Tiempo, " + gd.Key + ", Monto_Meta FROM " + stg + s.Goals.Table + ";");
            }
            sb.AppendLine("COMMIT TRANSACTION;");
            sb.AppendLine("END TRY");
            sb.AppendLine("BEGIN CATCH");
            sb.AppendLine("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            sb.AppendLine("THROW;");
            sb.AppendLine("END CATCH;");
            foreach (var n in StagingTables(s).AsEnumerable().Reverse()) sb.AppendLine("DROP TABLE " + stg + n + ";");
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
