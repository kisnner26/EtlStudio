using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;

namespace EtlStudio.Core
{
    public class PipelineResult
    {
        public string PackagePath, ReportPath, VerificationText;
        public bool ReconciliationOk = true;
        public List<KeyValuePair<Check, DataTable>> Checks = new List<KeyValuePair<Check, DataTable>>();
    }

    /// <summary>Orquesta todo el proceso a partir de un Plan aprobado: DSN -> DW -> paquete SSIS -> validar -> cargar -> verificar -> reporte.</summary>
    public static class Pipeline
    {
        public static readonly string[][] Steps =
        {
            new[] { "dsn", "Conectar con el origen" },
            new[] { "sql", "Conectar con SQL Server" },
            new[] { "profile", "Perfilar y validar el origen" },
            new[] { "dw", "Crear el Data Warehouse" },
            new[] { "pkg", "Generar el paquete SSIS" },
            new[] { "validate", "Validar el paquete" },
            new[] { "load", "Cargar los datos" },
            new[] { "verify", "Verificar resultados" },
            new[] { "report", "Crear el reporte (.docx)" }
        };

        /// <param name="step">(id, estado: run | ok | fail)</param>
        public static PipelineResult Run(Plan plan, bool recreate, string outDir, Action<string, string> step, Action<string> log, string packagePath = null)
        {
            var origStep = step; var origLog = log;
            step = (id, st) => { AppLog.Info("paso " + id + ": " + st); origStep(id, st); };
            log = m => { AppLog.Info(m); origLog(m); };
            AppLog.Info("Inicio de la corrida: " + plan.Spec.DwName + " (recrear=" + recreate + ")");
            try { var r = RunCore(plan, recreate, outDir, step, log, packagePath); AppLog.Info("Corrida terminada correctamente."); return r; }
            catch (Exception ex) { AppLog.Error("La corrida fallo", ex); throw; }
        }

        static PipelineResult RunCore(Plan plan, bool recreate, string outDir, Action<string, string> step, Action<string> log, string packagePath)
        {
            var s = plan.Spec; var res = new PipelineResult();
            Directory.CreateDirectory(outDir);
            Func<string, Action, bool> stage = (id, work) =>
            {
                step(id, "run");
                try { work(); step(id, "ok"); return true; }
                catch { step(id, "fail"); throw; }
            };

            stage("dsn", () =>
            {
                bool created = false;
                if (!Environments.DsnExists(s.SourceDsn))
                {
                    var c = plan.Conexion;
                    if (c == null || string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.HttpPath))
                        throw new Exception("No existe el DSN '" + s.SourceDsn + "' y el enunciado no trae host/HTTP path para crearlo. Crealo en el administrador ODBC.");
                    Environments.CreateUserDsn(s.SourceDsn, c.Host.Trim(), c.HttpPath.Trim()); created = true;
                    log("DSN '" + s.SourceDsn + "' creado (usuario) con host y HTTP path del enunciado.");
                }
                try { log(SourceReader.Test(s.SourceDsn)); }
                catch (Exception ex)
                {
                    if (created || ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 || ex.Message.IndexOf("authent", StringComparison.OrdinalIgnoreCase) >= 0 || ex.Message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0 || ex.Message.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new NeedsTokenException("Falta el token de acceso en el DSN '" + s.SourceDsn + "'. Abre el administrador ODBC (pestana DSN de usuario), Configure, pega tu token en Password y pulsa OK. Es un paso unico.");
                    throw;
                }
            });

            stage("sql", () => log(SqlRunner.Test(s.SqlServer)));

            // Antes de tocar nada: el origen debe cumplir lo que el modelo exige (sin nulos en llaves, fechas dentro de Dim_Tiempo...).
            IDictionary<string, object> profile = null;
            stage("profile", () =>
            {
                if (s.Goals != null && !File.Exists(s.Goals.ExcelPath)) throw new FileNotFoundException("No existe el Excel de metas: " + s.Goals.ExcelPath);
                profile = Reconciliation.ProfileSource(s);
                var issues = Reconciliation.Preflight(s, profile);
                if (issues.Count > 0) throw new Exception("El origen no cumple lo que exige el modelo:\n - " + string.Join("\n - ", issues));
                log("Origen validado: " + Convert.ToString(profile["n_rows"]) + " filas, sin nulos en las columnas que el modelo exige.");
            });

            stage("dw", () =>
            {
                if (recreate) SqlRunner.Exec(s.SqlServer, "master", DdlGenerator.DropDatabase(s));
                SqlRunner.Exec(s.SqlServer, "master", DdlGenerator.CreateDatabase(s));
                SqlRunner.Exec(s.SqlServer, s.DwName, DdlGenerator.Tables(s));
                SqlRunner.Exec(s.SqlServer, s.DwName, DdlGenerator.StagingPrepare(s));
                if (s.IsIncremental)
                {
                    // Carga incremental: se leen solo las filas con fecha >= la ultima cargada. Sin datos previos, se hace una carga completa.
                    var w = SqlRunner.Query(s.SqlServer, s.DwName, "SELECT MAX(t.Fecha) AS w FROM dbo." + s.Fact.Name + " f JOIN dbo." + s.Time.Name + " t ON t.SK_Tiempo = f.SK_Tiempo");
                    s.Watermark = w.Rows.Count > 0 && w.Rows[0][0] != DBNull.Value ? (DateTime?)Convert.ToDateTime(w.Rows[0][0]).Date : null;
                    log(s.Watermark.HasValue ? "Carga incremental desde " + s.Watermark.Value.ToString(DwSpec.DateFormat) + " (marca de agua)." : "Carga incremental sin datos previos: se carga todo el origen.");
                }   // el paquete escribe en stg; SSIS necesita que existan para leer su metadata
                log("Base '" + s.DwName + "' y tablas listas en " + s.SqlServer + ".");
            });

            res.PackagePath = packagePath ?? Path.Combine(outDir, s.DwName + ".dtsx");
            stage("pkg", () => { new SsisBuilder(s).Build(res.PackagePath); log("Paquete: " + res.PackagePath); });

            stage("validate", () => Dtx(res.PackagePath, true, log));
            stage("load", () => Dtx(res.PackagePath, false, log));

            // Si la reconciliacion no cuadra, igual se escribe el reporte (con la evidencia) y despues se avisa con una excepcion.
            ReconciliationException reconError = null;
            try
            {
                stage("verify", () =>
                {
                    var text = new System.Text.StringBuilder();
                    foreach (var c in Verification.For(s))
                    {
                        var dt = SqlRunner.Query(s.SqlServer, s.DwName, c.Sql);
                        res.Checks.Add(new KeyValuePair<Check, DataTable>(c, dt));
                        text.AppendLine("## " + c.Title).AppendLine(SourceReader.SampleRows(dt, 12));
                    }
                    var rc = Reconciliation.Run(s, profile);
                    var rt = rc.ToTable();
                    res.Checks.Add(new KeyValuePair<Check, DataTable>(new Check { Title = "R) Reconciliacion origen vs destino", Explain = Reconciliation.Explain, Sql = rc.Sql }, rt));
                    text.AppendLine("## R) Reconciliacion origen vs destino").AppendLine(SourceReader.SampleRows(rt, 30));
                    res.VerificationText = text.ToString();
                    res.ReconciliationOk = rc.Ok;
                    log("Verificaciones ejecutadas: " + res.Checks.Count + ". " + rc.Summary());
                    if (!rc.Ok) throw new ReconciliationException("Los datos cargados no coinciden con el origen:\n" + rc.FailureText());
                });
            }
            catch (ReconciliationException ex) { reconError = ex; }

            res.ReportPath = Path.Combine(outDir, "Reporte_" + s.DwName + ".docx");
            stage("report", () => { WriteReport(plan, res); log("Reporte: " + res.ReportPath); });
            if (reconError != null) throw new ReconciliationException(reconError.Message + "\nEl reporte con el detalle esta en: " + res.ReportPath);
            return res;
        }

        static void Dtx(string pkg, bool validate, Action<string> log)
        {
            int code = DtExec.Run(pkg, validate, l => { AppLog.Info("dtexec: " + l.Trim()); if (l.Contains("Error") || l.Contains("DTSER") || l.Contains("Elapsed")) log(l.Trim()); });
            if (code != 0) throw new Exception("dtexec devolvio el codigo " + code + " (" + (validate ? "validacion" : "carga") + ").");
        }

        static void WriteReport(Plan plan, PipelineResult res)
        {
            var s = plan.Spec;
            var d = new DocxWriter().Title(string.IsNullOrWhiteSpace(plan.Titulo) ? "Reporte del Data Warehouse" : plan.Titulo,
                "Generado por EtlStudio  ·  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            d.Heading("Resumen").Para(plan.Resumen ?? "");
            d.Para(res.ReconciliationOk ? "Reconciliacion origen vs destino: los datos cargados coinciden con el origen." : "Reconciliacion origen vs destino: NO COINCIDEN, revisa la seccion Verificacion.", true);
            d.Heading("Arquitectura");
            d.Bullet("Base de datos: " + s.DwName + " en " + s.SqlServer);
            d.Bullet("Origen: DSN " + s.SourceDsn + " (" + s.SourceLabel + ")");
            d.Bullet("Dimension de tiempo: " + s.Time.Name + " (" + s.Time.From + " a " + s.Time.To + ")");
            foreach (var dim in s.Dims) d.Bullet("Dimension " + dim.Name + " (" + dim.Key + "): " + string.Join(", ", dim.Columns.Select(c => c.Dst)));
            d.Bullet("Tabla de hechos " + s.Fact.Name + ": " + string.Join(", ", s.Fact.Measures.Select(m => m.Dst)));
            if (s.Goals != null) d.Bullet("Metas desde Excel: " + s.Goals.Table);
            d.Heading("Consulta de origen").Code(s.SourceQuery);
            d.Heading("Paquete SSIS").Para(res.PackagePath);
            d.Bullet("SQL_Auditoria_Inicio  →  SQL_Preparar_Staging  →  DFT_Poblar_Dimensiones  →  SQL_Fusionar_Dimensiones  →  DFT_Extraer_Origen  " + (s.Goals != null ? "→  DFT_Cargar_Metas_Excel  " : "") + "→  SQL_Publicar  →  SQL_Auditoria_Fin");
            d.Bullet("La carga escribe en tablas de paso (esquema stg) y SQL_Publicar las pasa al destino en una sola transaccion: si algo falla, el DW queda como estaba.");
            d.Bullet("Conversion de datos a DT_STR (DT_WSTR en NVARCHAR), Lookups por dimension (una llave sin dimension detiene la carga), columna derivada SK_Tiempo y carga rapida.");
            d.Bullet("Modo de carga: " + (s.IsIncremental ? "incremental (solo filas nuevas; marca de agua " + (s.Watermark.HasValue ? s.Watermark.Value.ToString(DwSpec.DateFormat) : "sin datos previos") + ")" : "completa") + ". Filas sin dimension: " + (s.RejectsRows ? "se rechazan a dbo." + DwSpec.RejectTableName + " y se cargan las demas." : "detienen la carga."));
            if (s.UsesUnknownMember) d.Bullet("Politica de nulos: un nulo en una columna de dimension se carga como '" + DwSpec.UnknownMember + "'.");
            d.Bullet("Cada corrida queda registrada en dbo." + DwSpec.AuditTableName + " (EnCurso, OK o Fallido).");
            d.Heading("Verificacion");
            foreach (var kv in res.Checks)
            {
                d.Para(kv.Key.Title, true).Para(kv.Key.Explain).Table(kv.Value);
            }
            if (plan.Advertencias.Count > 0) { d.Heading("Supuestos y advertencias"); foreach (var a in plan.Advertencias) d.Bullet(a); }
            d.Save(res.ReportPath);
        }
    }
}
