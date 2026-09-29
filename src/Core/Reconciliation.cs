using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Linq;

namespace EtlStudio.Core
{
    public class ReconLine { public string Metric, Source, Dest, Status; }

    /// <summary>El DW cargado no coincide con el origen (filas perdidas, sumas distintas...).</summary>
    public class ReconciliationException : Exception { public ReconciliationException(string m) : base(m) { } }

    public class ReconResult
    {
        public List<ReconLine> Lines = new List<ReconLine>();
        public string Sql = "";

        public bool Ok { get { return Lines.All(l => l.Status != "DIFERENCIA"); } }

        public DataTable ToTable()
        {
            var t = new DataTable("Reconciliacion");
            foreach (var c in new[] { "Metrica", "Origen", "Destino", "Estado" }) t.Columns.Add(c, typeof(string));
            foreach (var l in Lines) t.Rows.Add(l.Metric, l.Source, l.Dest, l.Status);
            return t;
        }

        public string Summary()
        {
            int bad = Lines.Count(l => l.Status == "DIFERENCIA"), warn = Lines.Count(l => l.Status == "AVISO");
            return (bad == 0 ? "Reconciliacion correcta" : "RECONCILIACION FALLIDA (" + bad + " diferencias)") + " - " + Lines.Count + " comprobaciones" + (warn > 0 ? ", " + warn + " avisos." : ".");
        }

        public string FailureText()
        {
            return string.Join("\n", Lines.Where(l => l.Status == "DIFERENCIA").Select(l => " - " + l.Metric + ": origen " + l.Source + ", destino " + l.Dest));
        }
    }

    /// <summary>
    /// Compara el origen contra el DW cargado (filas, llaves de negocio, rango de fechas y suma de cada medida) y detecta
    /// antes de cargar los datos que el modelo no admite (nulos, fechas fuera de la dimension tiempo).
    /// El SQL del origen usa solo construcciones comunes a Spark SQL y T-SQL.
    /// </summary>
    public static class Reconciliation
    {
        public const string Explain = "Compara el origen con el DW: filas, llaves de negocio distintas, fecha minima y maxima, y la suma de cada medida. " +
                                      "Ademas compara cada anio-mes (filas y sumas), porque los totales pueden cuadrar aunque haya errores que se compensan. " +
                                      "Cualquier diferencia significa que se perdieron o alteraron datos en la carga.";

        /// <summary>Columnas del origen que el modelo exige NOT NULL (llave de negocio, fecha, dimensiones y medidas).</summary>
        public static List<string> NullCheckedColumns(DwSpec s)
        {
            var l = new List<string> { s.Fact.BusinessKey.Src, s.Fact.DateColumn };
            if (!s.UsesUnknownMember) foreach (var d in s.Dims) l.AddRange(d.Columns.Select(c => c.Src));   // con 'Unknown' un nulo de dimension se carga como 'Desconocido'
            l.AddRange(s.Fact.Measures.Select(m => m.Src));
            return l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string SourceSql(DwSpec s)
        {
            var f = s.Fact;
            var p = new List<string>
            {
                "COUNT(*) AS n_rows",
                "COUNT(DISTINCT s." + f.BusinessKey.Src + ") AS n_bk",
                "MIN(s." + f.DateColumn + ") AS min_date",
                "MAX(s." + f.DateColumn + ") AS max_date"
            };
            foreach (var c in NullCheckedColumns(s)) p.Add("SUM(CASE WHEN s." + c + " IS NULL THEN 1 ELSE 0 END) AS null_" + c);
            foreach (var m in f.Measures) p.Add("SUM(s." + m.Src + ") AS sum_" + m.Src);
            return "SELECT " + string.Join(",\n       ", p) + "\nFROM (" + s.SourceQuery + ") AS s";
        }

        public static string DestSql(DwSpec s)
        {
            var f = s.Fact;
            var p = new List<string>
            {
                "COUNT(*) AS n_rows",
                "COUNT(DISTINCT f." + f.BusinessKey.Dst + ") AS n_bk",
                "MIN(t.Fecha) AS min_date",
                "MAX(t.Fecha) AS max_date"
            };
            foreach (var m in f.Measures) p.Add("SUM(CAST(f." + m.Dst + " AS DECIMAL(38,6))) AS sum_" + m.Src);
            if (s.RejectsRows)
            {
                // lo rechazado tambien cuenta: origen = cargado + rechazado
                string r = "dbo." + DwSpec.RejectTableName;
                p.Add("(SELECT COUNT_BIG(*) FROM " + r + ") AS rej_rows");
                p.Add("(SELECT COUNT(DISTINCT s_" + f.BusinessKey.Src + ") FROM " + r + ") AS rej_bk");
                p.Add("(SELECT MIN(CAST(CAST(SK_Tiempo AS VARCHAR(8)) AS DATE)) FROM " + r + ") AS rej_min_date");
                p.Add("(SELECT MAX(CAST(CAST(SK_Tiempo AS VARCHAR(8)) AS DATE)) FROM " + r + ") AS rej_max_date");
                foreach (var m in f.Measures) p.Add("(SELECT SUM(CAST(s_" + m.Src + " AS DECIMAL(38,6))) FROM " + r + ") AS rej_sum_" + m.Src);
            }
            return "SELECT " + string.Join(",\n       ", p) + "\nFROM dbo." + f.Name + " f\nJOIN dbo." + s.Time.Name + " t ON t.SK_Tiempo = f.SK_Tiempo";
        }

        // ---------------- conversion tolerante ----------------

        static object Get(IDictionary<string, object> d, string key)
        {
            object v; return d != null && d.TryGetValue(key, out v) && v != DBNull.Value ? v : null;
        }

        static long ToLong(object v) { return v == null ? 0 : Convert.ToInt64(Convert.ToDecimal(v, CultureInfo.InvariantCulture)); }

        static decimal ToDec(object v) { return v == null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture); }

        static DateTime? ToDate(object v)
        {
            if (v == null) return null;
            if (v is DateTime) return ((DateTime)v).Date;
            DateTime d;
            return DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d.Date : null;
        }

        static string Num(decimal d) { return d.ToString("N2", CultureInfo.InvariantCulture); }

        static string Day(DateTime? d) { return d.HasValue ? d.Value.ToString(DwSpec.DateFormat, CultureInfo.InvariantCulture) : "(sin dato)"; }

        public static IDictionary<string, object> ReadRow(DataTable dt)
        {
            var r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (dt != null && dt.Rows.Count > 0)
                foreach (DataColumn c in dt.Columns) r[c.ColumnName] = dt.Rows[0][c] == DBNull.Value ? null : dt.Rows[0][c];
            return r;
        }

        // ---------------- comprobaciones puras (probadas sin bases de datos) ----------------

        /// <summary>Problemas del origen que harian fallar o falsear la carga. Vacio = se puede cargar.</summary>
        public static List<string> Preflight(DwSpec s, IDictionary<string, object> src)
        {
            var errs = new List<string>();
            if (ToLong(Get(src, "n_rows")) == 0) errs.Add("El origen no devolvio ninguna fila.");
            foreach (var c in NullCheckedColumns(s))
            {
                long n = ToLong(Get(src, "null_" + c));
                if (n > 0) errs.Add("La columna '" + c + "' tiene " + n.ToString("N0", CultureInfo.InvariantCulture) + " valores nulos y el modelo la exige NOT NULL (corrige el origen o filtralos en el SELECT).");
            }
            DateTime from, to;
            if (!s.RejectsRows && DateTime.TryParseExact(s.Time.From, DwSpec.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out from) &&
                DateTime.TryParseExact(s.Time.To, DwSpec.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out to))
            {
                var min = ToDate(Get(src, "min_date")); var max = ToDate(Get(src, "max_date"));
                if ((min.HasValue && min.Value < from) || (max.HasValue && max.Value > to))
                    errs.Add("Hay fechas fuera de " + s.Time.Name + ": el origen va de " + Day(min) + " a " + Day(max) + " y la dimension de " + s.Time.From + " a " + s.Time.To + ". Amplia Time.From / Time.To.");
            }
            return errs;
        }

        /// <summary>Compara el perfil del origen con el del destino. Las diferencias quedan con estado DIFERENCIA.</summary>
        public static ReconResult Compare(DwSpec s, IDictionary<string, object> src, IDictionary<string, object> dst)
        {
            var r = new ReconResult { Sql = SourceSql(s) + "\n\n" + DestSql(s) };
            Action<string, string, string, bool> add = (m, a, b, ok) => r.Lines.Add(new ReconLine { Metric = m, Source = a, Dest = b, Status = ok ? "OK" : "DIFERENCIA" });

            long rr = ToLong(Get(dst, "rej_rows")), rbk = ToLong(Get(dst, "rej_bk"));
            long sr = ToLong(Get(src, "n_rows")), dr = ToLong(Get(dst, "n_rows"));
            add("Filas de " + s.Fact.Name + (rr > 0 ? " (cargadas + rechazadas)" : ""), sr.ToString("N0", CultureInfo.InvariantCulture), (dr + rr).ToString("N0", CultureInfo.InvariantCulture), sr == dr + rr);
            if (rr > 0)
                r.Lines.Add(new ReconLine { Metric = "Filas rechazadas (revisa dbo." + DwSpec.RejectTableName + ")", Source = "-", Dest = rr.ToString("N0", CultureInfo.InvariantCulture), Status = "AVISO" });

            long sb = ToLong(Get(src, "n_bk")), db = ToLong(Get(dst, "n_bk")) + rbk;
            add("Llaves de negocio distintas (" + s.Fact.BusinessKey.Dst + ")", sb.ToString("N0", CultureInfo.InvariantCulture), db.ToString("N0", CultureInfo.InvariantCulture), sb == db);
            if (sb < sr)
                r.Lines.Add(new ReconLine { Metric = "Llaves de negocio repetidas en el origen", Source = (sr - sb).ToString("N0", CultureInfo.InvariantCulture), Dest = "-", Status = "AVISO" });

            var smin = ToDate(Get(src, "min_date")); var dmin = Earlier(ToDate(Get(dst, "min_date")), ToDate(Get(dst, "rej_min_date")));
            var smax = ToDate(Get(src, "max_date")); var dmax = Later(ToDate(Get(dst, "max_date")), ToDate(Get(dst, "rej_max_date")));
            add("Fecha minima (" + s.Fact.DateColumn + ")", Day(smin), Day(dmin), smin == dmin);
            add("Fecha maxima (" + s.Fact.DateColumn + ")", Day(smax), Day(dmax), smax == dmax);

            // Cada valor se redondea al convertirse a DECIMAL(x,2): hasta media centesima de error por fila.
            decimal tol = 0.005m * Math.Max(sr, dr) + 0.01m;
            foreach (var m in s.Fact.Measures)
            {
                decimal a = ToDec(Get(src, "sum_" + m.Src)), b = ToDec(Get(dst, "sum_" + m.Src)) + ToDec(Get(dst, "rej_sum_" + m.Src));
                add("Suma de " + m.Dst, Num(a), Num(b), Math.Abs(a - b) <= tol);
            }
            return r;
        }

        static DateTime? Earlier(DateTime? a, DateTime? b) { return !a.HasValue ? b : !b.HasValue ? a : (a.Value < b.Value ? a : b); }

        static DateTime? Later(DateTime? a, DateTime? b) { return !a.HasValue ? b : !b.HasValue ? a : (a.Value > b.Value ? a : b); }

        // ---------------- reconciliacion por periodo ----------------
        // Los totales pueden cuadrar aunque haya errores que se compensan. Comparar cada anio-mes los delata (YEAR/MONTH existen en Spark SQL y T-SQL).

        public static string PeriodSourceSql(DwSpec s)
        {
            string d = "s." + s.Fact.DateColumn;
            var p = new List<string> { "YEAR(" + d + ") AS p_y", "MONTH(" + d + ") AS p_m", "COUNT(*) AS n_rows" };
            foreach (var m in s.Fact.Measures) p.Add("SUM(s." + m.Src + ") AS sum_" + m.Src);
            return "SELECT " + string.Join(", ", p) + "\nFROM (" + s.SourceQuery + ") AS s\nGROUP BY YEAR(" + d + "), MONTH(" + d + ")";
        }

        public static string PeriodDestSql(DwSpec s)
        {
            var f = s.Fact; var cols = new List<string> { "COUNT(*) AS n_rows" }; var outer = new List<string> { "p_y", "p_m", "SUM(n_rows) AS n_rows" };
            foreach (var m in f.Measures) { cols.Add("SUM(CAST(f." + m.Dst + " AS DECIMAL(38,6))) AS sum_" + m.Src); outer.Add("SUM(sum_" + m.Src + ") AS sum_" + m.Src); }
            string inner = "SELECT t.Anio AS p_y, t.Mes AS p_m, " + string.Join(", ", cols) + " FROM dbo." + f.Name + " f JOIN dbo." + s.Time.Name + " t ON t.SK_Tiempo = f.SK_Tiempo GROUP BY t.Anio, t.Mes";
            if (s.RejectsRows)
            {
                var rc = new List<string> { "COUNT(*) AS n_rows" };
                foreach (var m in f.Measures) rc.Add("SUM(CAST(r.s_" + m.Src + " AS DECIMAL(38,6))) AS sum_" + m.Src);
                inner += " UNION ALL SELECT r.SK_Tiempo / 10000, (r.SK_Tiempo / 100) % 100, " + string.Join(", ", rc) + " FROM dbo." + DwSpec.RejectTableName + " r GROUP BY r.SK_Tiempo / 10000, (r.SK_Tiempo / 100) % 100";
            }
            return "SELECT " + string.Join(", ", outer) + "\nFROM (" + inner + ") u\nGROUP BY p_y, p_m";
        }

        /// <summary>Compara cada anio-mes (filas y sumas). Devuelve una linea por periodo distinto y una de resumen.</summary>
        public static List<ReconLine> ComparePeriods(DwSpec s, DataTable src, DataTable dst)
        {
            Func<DataTable, Dictionary<string, DataRow>> index = t =>
            {
                var d = new Dictionary<string, DataRow>();
                if (t != null) foreach (DataRow r in t.Rows) d[Convert.ToInt32(r["p_y"], CultureInfo.InvariantCulture) + "-" + Convert.ToInt32(r["p_m"], CultureInfo.InvariantCulture).ToString("00")] = r;
                return d;
            };
            var a = index(src); var b = index(dst); var lines = new List<ReconLine>(); int bad = 0;
            foreach (var k in a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                DataRow ra, rb; a.TryGetValue(k, out ra); b.TryGetValue(k, out rb);
                Func<DataRow, string, object> val = (r, c) => r == null || r[c] == DBNull.Value ? null : r[c];
                long na = ToLong(val(ra, "n_rows")), nb = ToLong(val(rb, "n_rows"));
                var diffs = new List<string>();
                if (na != nb) diffs.Add("filas " + na.ToString("N0", CultureInfo.InvariantCulture) + " vs " + nb.ToString("N0", CultureInfo.InvariantCulture));
                decimal tol = 0.005m * Math.Max(na, nb) + 0.01m;
                foreach (var m in s.Fact.Measures)
                {
                    decimal x = ToDec(val(ra, "sum_" + m.Src)), y = ToDec(val(rb, "sum_" + m.Src));
                    if (Math.Abs(x - y) > tol) diffs.Add(m.Dst + " " + Num(x) + " vs " + Num(y));
                }
                if (diffs.Count == 0) continue;
                if (++bad <= 20) lines.Add(new ReconLine { Metric = "Periodo " + k, Source = string.Join("; ", diffs), Dest = "(origen vs destino)", Status = "DIFERENCIA" });
            }
            if (bad > 20) lines.Add(new ReconLine { Metric = "Otros periodos con diferencias", Source = (bad - 20).ToString(CultureInfo.InvariantCulture), Dest = "-", Status = "DIFERENCIA" });
            lines.Insert(0, new ReconLine { Metric = "Periodos (anio-mes) comparados", Source = a.Count.ToString(CultureInfo.InvariantCulture), Dest = b.Count.ToString(CultureInfo.InvariantCulture), Status = bad == 0 && a.Count == b.Count ? "OK" : "DIFERENCIA" });
            return lines;
        }

        // ---------------- acceso a datos ----------------

        public static IDictionary<string, object> ProfileSource(DwSpec s) { return ReadRow(SourceReader.Query(s.SourceDsn, SourceSql(s))); }

        public static IDictionary<string, object> ProfileDestination(DwSpec s) { return ReadRow(SqlRunner.Query(s.SqlServer, s.DwName, DestSql(s))); }

        /// <summary>Metas del Excel contra la tabla de metas: mismas filas y misma suma.</summary>
        public static List<ReconLine> GoalsLines(DwSpec s)
        {
            var g = s.Goals; var lines = new List<ReconLine>();
            try
            {
                long xr; decimal xs;
                var cs = "Provider=Microsoft.ACE.OLEDB.16.0;Data Source=" + g.ExcelPath + ";Extended Properties=\"Excel 12.0;HDR=YES\";";
                using (var c = new OleDbConnection(cs))
                {
                    c.Open();
                    var cmd = new OleDbCommand("SELECT COUNT([" + g.ExcelDimColumn + "]), SUM([" + g.ExcelMeasureColumn + "]) FROM [" + g.Sheet + "]", c);
                    using (var rd = cmd.ExecuteReader()) { rd.Read(); xr = ToLong(rd.IsDBNull(0) ? null : rd[0]); xs = ToDec(rd.IsDBNull(1) ? null : rd[1]); }
                }
                var d = ReadRow(SqlRunner.Query(s.SqlServer, s.DwName, "SELECT COUNT(*) AS n, SUM(CAST(Monto_Meta AS DECIMAL(38,6))) AS s FROM dbo." + g.Table));
                long dr = ToLong(Get(d, "n")); decimal ds = ToDec(Get(d, "s"));
                lines.Add(new ReconLine { Metric = "Filas de " + g.Table + " (Excel)", Source = xr.ToString("N0", CultureInfo.InvariantCulture), Dest = dr.ToString("N0", CultureInfo.InvariantCulture), Status = xr == dr ? "OK" : "DIFERENCIA" });
                lines.Add(new ReconLine { Metric = "Suma de Monto_Meta (Excel)", Source = Num(xs), Dest = Num(ds), Status = Math.Abs(xs - ds) <= 0.005m * Math.Max(xr, dr) + 0.01m ? "OK" : "DIFERENCIA" });
            }
            catch (Exception ex)
            {
                lines.Add(new ReconLine { Metric = "Metas: no se pudo comparar con el Excel", Source = ex.Message.Replace('\n', ' '), Dest = "-", Status = "AVISO" });
            }
            return lines;
        }

        /// <summary>Reconcilia el DW ya cargado contra el origen. Si ya tienes el perfil del origen, pasalo para no volver a leerlo.</summary>
        public static ReconResult Run(DwSpec s, IDictionary<string, object> sourceProfile = null)
        {
            var r = Compare(s, sourceProfile ?? ProfileSource(s), ProfileDestination(s));
            try
            {
                r.Lines.AddRange(ComparePeriods(s, SourceReader.Query(s.SourceDsn, PeriodSourceSql(s)), SqlRunner.Query(s.SqlServer, s.DwName, PeriodDestSql(s))));
                r.Sql += "\n\n" + PeriodSourceSql(s) + "\n\n" + PeriodDestSql(s);
            }
            catch (Exception ex) { r.Lines.Add(new ReconLine { Metric = "No se pudo comparar por periodo", Source = ex.Message.Replace('\n', ' '), Dest = "-", Status = "AVISO" }); }
            if (s.Goals != null) r.Lines.AddRange(GoalsLines(s));
            return r;
        }

        /// <summary>Resultado de reemplazo cuando la reconciliacion no pudo ejecutarse (por ejemplo, el origen no responde).</summary>
        public static ReconResult Failed(string reason)
        {
            var r = new ReconResult();
            r.Lines.Add(new ReconLine { Metric = "No se pudo reconciliar", Source = (reason ?? "").Replace('\n', ' '), Dest = "-", Status = "AVISO" });
            return r;
        }
    }
}
