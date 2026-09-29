using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using EtlStudio.Core;

/// <summary>
/// Pruebas del nucleo que no necesitan SQL Server, SSIS ni ODBC: validacion del diseno, DDL, SQL de verificacion
/// y logica de reconciliacion. Se ejecutan con tests/run.sh (Mono) o tests\run.ps1 (Windows).
/// </summary>
public static class CoreTests
{
    static int passed, failed;
    static string current = "";

    static void Check(bool cond, string what)
    {
        if (cond) { passed++; return; }
        failed++; Console.WriteLine("  FALLA [" + current + "] " + what);
    }

    static void Test(string name, Action body)
    {
        current = name;
        try { body(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPCION [" + name + "] " + ex.GetType().Name + ": " + ex.Message); }
    }

    static string Root()
    {
        var d = AppDomain.CurrentDomain.BaseDirectory;
        while (d != null && !Directory.Exists(Path.Combine(d, "samples"))) d = Path.GetDirectoryName(d);
        if (d == null) throw new DirectoryNotFoundException("No se encontro la carpeta 'samples'.");
        return d;
    }

    static DwSpec Sample() { return DwSpec.FromJson(File.ReadAllText(Path.Combine(Root(), "samples", "autos_databricks.json"))); }

    static bool Has(List<string> errs, string part) { return errs.Any(e => e.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0); }

    static Dictionary<string, object> Row(params object[] kv)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }

    /// <summary>Perfil de origen y destino coherentes para el sample (30 filas).</summary>
    static Dictionary<string, object> GoodProfile(DwSpec s)
    {
        var r = Row("n_rows", 30L, "n_bk", 30L, "min_date", new DateTime(2024, 1, 5), "max_date", new DateTime(2024, 12, 20));
        foreach (var c in Reconciliation.NullCheckedColumns(s)) r["null_" + c] = 0L;
        foreach (var m in s.Fact.Measures) r["sum_" + m.Src] = 1000.50m;
        return r;
    }

    static Dictionary<string, object> GoodDest(DwSpec s)
    {
        var r = Row("n_rows", 30L, "n_bk", 30L, "min_date", new DateTime(2024, 1, 5), "max_date", new DateTime(2024, 12, 20));
        foreach (var m in s.Fact.Measures) r["sum_" + m.Src] = 1000.50m;
        return r;
    }

    public static int Main()
    {
        // ---------------- validacion del diseno ----------------
        Test("el sample valida", () => { var e = Sample().Validate(); Check(e.Count == 0, "el sample deberia ser valido: " + string.Join("; ", e)); });
        Test("el ejemplo interno valida", () => { var e = DwSpec.ExampleAutos().Validate(); Check(e.Count == 0, string.Join("; ", e)); });
        Test("ida y vuelta JSON", () => { var a = Sample(); var b = DwSpec.FromJson(a.ToJson()); Check(a.ToJson() == b.ToJson(), "el JSON cambia al reserializar"); });

        Test("rechaza inyeccion en identificadores", () => { var s = Sample(); s.DwName = "x]; DROP DATABASE master;--"; Check(Has(s.Validate(), "DwName"), "DwName"); });
        Test("rechaza tipo SQL fuera de la lista", () => { var s = Sample(); s.Fact.Measures[0].Type = "NVARCHAR(MAX)"; Check(Has(s.Validate(), "Tipo SQL"), "tipo"); });
        Test("rechaza VARCHAR(0) y VARCHAR(9000)", () =>
        {
            var s = Sample(); s.Dims[0].Columns[0].Type = "VARCHAR(0)"; Check(Has(s.Validate(), "fuera de rango"), "0");
            s = Sample(); s.Dims[0].Columns[0].Type = "VARCHAR(9000)"; Check(Has(s.Validate(), "fuera de rango"), "9000");
        });
        Test("rechaza DECIMAL con escala mayor que precision", () => { var s = Sample(); s.Fact.Measures[0].Type = "DECIMAL(5,9)"; Check(Has(s.Validate(), "fuera de rango"), "decimal"); });
        Test("las medidas deben ser numericas", () => { var s = Sample(); s.Fact.Measures[0].Type = "VARCHAR(10)"; Check(Has(s.Validate(), "numericas"), "medida"); });
        Test("SourceQuery: solo SELECT, sin ; ni comentarios", () =>
        {
            var s = Sample(); s.SourceQuery = "DELETE FROM t"; Check(Has(s.Validate(), "debe ser un SELECT"), "select");
            s = Sample(); s.SourceQuery += "; DROP TABLE x"; Check(Has(s.Validate(), "';'"), "punto y coma");
            s = Sample(); s.SourceQuery += " -- comentario"; Check(Has(s.Validate(), "comentarios"), "comentario --");
            s = Sample(); s.SourceQuery += " /* x */"; Check(Has(s.Validate(), "comentarios"), "comentario /* */");
        });
        Test("SqlServer y DSN no admiten inyeccion en la cadena de conexion", () =>
        {
            var s = Sample(); s.SqlServer = "localhost;Integrated Security=False"; Check(Has(s.Validate(), "SqlServer"), "server");
            s = Sample(); s.SourceDsn = "d;PWD=x"; Check(Has(s.Validate(), "SourceDsn"), "dsn");
        });
        Test("fechas de Time: formato exacto y rango", () =>
        {
            var s = Sample(); s.Time.From = "01/02/2023"; Check(Has(s.Validate(), "Rango de Time"), "formato");
            s = Sample(); s.Time.From = "2031-01-01"; Check(Has(s.Validate(), "Rango de Time"), "invertido");
            s = Sample(); s.Time.From = "1800-01-01"; s.Time.To = "2999-12-31"; Check(Has(s.Validate(), "demasiado grande"), "enorme");
        });
        Test("nombres reservados y repetidos", () =>
        {
            var s = Sample(); s.Dims[1].Columns[1].Dst = "Fecha_Creacion"; Check(Has(s.Validate(), "reservado"), "reservado");
            s = Sample(); s.Dims[1].Columns[1].Dst = s.Dims[1].Columns[2].Dst; Check(Has(s.Validate(), "Nombre repetido"), "columna repetida");
            s = Sample(); s.Dims[1].Name = s.Dims[0].Name; Check(Has(s.Validate(), "tablas del DW"), "tabla repetida");
            s = Sample(); s.Dims[1].Key = s.Dims[0].Key; Check(Has(s.Validate(), "llaves"), "llave repetida");
            s = Sample(); s.Fact.Name = "ETL_Auditoria"; Check(Has(s.Validate(), "tablas del DW"), "choca con auditoria");
        });
        Test("columnas calculadas: solo medidas y funciones permitidas", () =>
        {
            var s = Sample(); s.Fact.Computed[0].Expr = "(Precio_Lista + Otra_Columna)"; Check(Has(s.Validate(), "Otra_Columna"), "columna desconocida");
            s = Sample(); s.Fact.Computed[0].Expr = "(SELECT 1)"; Check(s.Validate().Count > 0, "subconsulta");
            s = Sample(); s.Fact.Computed[0].Expr = "ISNULL(Precio_Lista, 0) * 1.16"; Check(s.Validate().Count == 0, "funcion permitida: " + string.Join("; ", s.Validate()));
        });
        Test("metas: dimension existente y datos de Excel seguros", () =>
        {
            var s = Sample(); s.Goals.DimName = "Dim_Nada"; Check(Has(s.Validate(), "Goals.DimName"), "dim");
            s = Sample(); s.Goals.ExcelPath = "C:\\x.xlsx\";Extended Properties=\"x"; Check(Has(s.Validate(), "ExcelPath"), "ruta");
            s = Sample(); s.Goals.Sheet = "Hoja]; DROP"; Check(Has(s.Validate(), "Goals.Sheet"), "hoja");
            s = Sample(); s.Goals.ExcelMeasureColumn = "Monto]x"; Check(Has(s.Validate(), "Columna de Excel"), "columna excel");
            s = Sample(); s.Goals = null; Check(s.Validate().Count == 0, "las metas son opcionales");
        });

        // ---------------- DDL ----------------
        Test("DDL: tablas, llaves y auditoria", () =>
        {
            var sql = DdlGenerator.Tables(Sample());
            foreach (var t in new[] { "dbo.Dim_Tiempo", "dbo.Dim_Sucursal", "dbo.Dim_Vehiculo", "dbo.Fact_VentasAutos", "dbo.Fact_MetasVentas", "dbo.ETL_Auditoria" })
                Check(sql.Contains(t), "falta " + t);
            Check(sql.Contains("SK_Sucursal INT IDENTITY(1,1)"), "identity de la dimension");
            Check(sql.Contains("Ingreso_Total AS (Precio_Lista + Extras_Instalados) PERSISTED"), "columna calculada");
            Check(sql.Contains("FK_Fact_VentasAutos_Dim_Vehiculo"), "FK a la dimension");
        });
        Test("DDL: cleanup respeta el orden de FKs y no toca la auditoria", () =>
        {
            var sql = DdlGenerator.Cleanup(Sample());
            Check(sql.IndexOf("DELETE FROM dbo.Fact_MetasVentas") < sql.IndexOf("DELETE FROM dbo.Fact_VentasAutos"), "metas antes que la fact");
            Check(sql.IndexOf("DELETE FROM dbo.Fact_VentasAutos") < sql.IndexOf("DELETE FROM dbo.Dim_Sucursal"), "fact antes que dimensiones");
            Check(!sql.Contains("ETL_Auditoria"), "la limpieza no debe borrar la auditoria");
        });
        Test("auditoria: inicio cierra corridas abiertas y fin las cuenta", () =>
        {
            var s = Sample(); var a = DdlGenerator.AuditStart(s); var z = DdlGenerator.AuditEnd(s);
            Check(a.Contains("CREATE TABLE dbo.ETL_Auditoria") && a.Contains("'Fallido'") && a.Contains("'EnCurso'"), "inicio");
            Check(z.Contains("Estado = 'OK'") && z.Contains("Dim_Sucursal") && z.Contains("Dim_Vehiculo") && z.Contains("Fact_VentasAutos"), "fin");
        });

        // ---------------- verificacion ----------------
        Test("verificacion: la primera consulta son los conteos (la interfaz lo asume)", () =>
        {
            var l = Verification.For(Sample());
            Check(l.Count == 7, "con metas debe haber 7 consultas, hay " + l.Count);
            Check(l[0].Title.StartsWith("1)") && l[0].Sql.Contains("COUNT(*)"), "conteos primero");
            s_noGoals(l);
        });

        // ---------------- reconciliacion ----------------
        Test("SQL de perfil del origen: agregados portables", () =>
        {
            var sql = Reconciliation.SourceSql(Sample());
            Check(sql.StartsWith("SELECT COUNT(*) AS n_rows"), "empieza con el conteo");
            Check(sql.Contains("FROM (SELECT id_venta") && sql.EndsWith(") AS s"), "envuelve el SELECT de origen");
            Check(sql.Contains("COUNT(DISTINCT s.id_venta) AS n_bk"), "llaves distintas");
            Check(sql.Contains("SUM(s.precio_lista) AS sum_precio_lista"), "suma de medidas");
            Check(sql.Contains("null_fecha_venta") && sql.Contains("null_vin_corto") && sql.Contains("null_comision_vendedor"), "nulos de fecha, dimension y medida");
            Check(!sql.Contains("LIMIT") && !sql.Contains("TOP"), "sin sintaxis propia de un motor");
        });
        Test("SQL del destino", () =>
        {
            var sql = Reconciliation.DestSql(Sample());
            Check(sql.Contains("FROM dbo.Fact_VentasAutos f") && sql.Contains("JOIN dbo.Dim_Tiempo t ON t.SK_Tiempo = f.SK_Tiempo"), "join con tiempo");
            Check(sql.Contains("AS sum_precio_lista"), "la suma usa el nombre del origen para poder compararse");
        });
        Test("columnas revisadas por nulos: sin repetidos", () =>
        {
            var cols = Reconciliation.NullCheckedColumns(Sample());
            Check(cols.Count == cols.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "repetidos");
            Check(cols.Contains("id_venta") && cols.Contains("fecha_venta") && cols.Contains("marca") && cols.Contains("precio_lista"), "contenido");
        });
        Test("preflight: origen sano", () => Check(Reconciliation.Preflight(Sample(), GoodProfile(Sample())).Count == 0, "no deberia haber problemas"));
        Test("preflight: origen vacio", () => { var p = GoodProfile(Sample()); p["n_rows"] = 0L; Check(Has(Reconciliation.Preflight(Sample(), p), "ninguna fila"), "vacio"); });
        Test("preflight: nulos en una dimension", () =>
        {
            var s = Sample(); var p = GoodProfile(s); p["null_marca"] = 12L;
            var e = Reconciliation.Preflight(s, p); Check(e.Count == 1 && Has(e, "'marca'") && Has(e, "12"), "nulos: " + string.Join("; ", e));
        });
        Test("preflight: fechas fuera de la dimension tiempo", () =>
        {
            var s = Sample(); var p = GoodProfile(s); p["min_date"] = new DateTime(2019, 3, 1);
            Check(Has(Reconciliation.Preflight(s, p), "fuera de Dim_Tiempo"), "antes");
            p = GoodProfile(s); p["max_date"] = "2035-06-01";
            Check(Has(Reconciliation.Preflight(s, p), "fuera de Dim_Tiempo"), "despues (fecha como texto)");
        });
        Test("comparar: coincide", () => { var r = Reconciliation.Compare(Sample(), GoodProfile(Sample()), GoodDest(Sample())); Check(r.Ok, r.FailureText()); Check(r.Lines.All(l => l.Status == "OK"), "todas OK"); });
        Test("comparar: detecta filas perdidas", () =>
        {
            var d = GoodDest(Sample()); d["n_rows"] = 29L;
            var r = Reconciliation.Compare(Sample(), GoodProfile(Sample()), d);
            Check(!r.Ok && r.FailureText().Contains("Filas de Fact_VentasAutos"), "deberia fallar por filas");
        });
        Test("comparar: detecta una suma alterada", () =>
        {
            var s = Sample(); var d = GoodDest(s); d["sum_costo_fabricacion"] = 990.00m;
            var r = Reconciliation.Compare(s, GoodProfile(s), d);
            Check(!r.Ok && r.FailureText().Contains("Suma de Costo_Fabricacion") && !r.FailureText().Contains("Suma de Precio_Lista"), r.FailureText());
        });
        Test("comparar: tolera el redondeo a 2 decimales", () =>
        {
            var s = Sample(); var d = GoodDest(s); d["sum_precio_lista"] = 1000.50m + 0.004m;
            Check(Reconciliation.Compare(s, GoodProfile(s), d).Ok, "media centesima de diferencia es redondeo");
        });
        Test("comparar: detecta el rango de fechas distinto", () =>
        {
            var d = GoodDest(Sample()); d["max_date"] = new DateTime(2024, 12, 19);
            Check(!Reconciliation.Compare(Sample(), GoodProfile(Sample()), d).Ok, "fecha maxima");
        });
        Test("comparar: llaves repetidas en el origen son un aviso, no un error", () =>
        {
            var s = Sample(); var p = GoodProfile(s); p["n_bk"] = 28L; var d = GoodDest(s); d["n_bk"] = 28L;
            var r = Reconciliation.Compare(s, p, d); Check(r.Ok && r.Lines.Any(l => l.Status == "AVISO"), "aviso");
        });
        Test("comparar: destino vacio no se confunde con coincidencia", () =>
        {
            var r = Reconciliation.Compare(Sample(), GoodProfile(Sample()), Row());
            Check(!r.Ok, "un destino sin datos debe fallar");
        });
        Test("tabla de resultado y resumen", () =>
        {
            var d = GoodDest(Sample()); d["n_rows"] = 1L;
            var r = Reconciliation.Compare(Sample(), GoodProfile(Sample()), d); var t = r.ToTable();
            Check(t.Columns.Count == 4 && t.Rows.Count == r.Lines.Count, "tabla");
            Check(r.Summary().StartsWith("RECONCILIACION FALLIDA"), "resumen");
            Check(Reconciliation.Failed("sin red").Ok, "un aviso por si solo no rompe el flujo manual");
        });

        // ---------------- plan ----------------
        Test("plan: completa los campos que la IA omita", () =>
        {
            var p = Plan.FromJson("{\"Spec\":" + Sample().ToJson() + "}");
            Check(p.Conexion != null && p.Pasos != null && p.Advertencias != null, "listas por defecto");
        });

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "OK: " + passed + " comprobaciones correctas." : "FALLARON " + failed + " de " + (passed + failed) + " comprobaciones.");
        return failed == 0 ? 0 : 1;
    }

    static void s_noGoals(List<Check> withGoals)
    {
        var s = Sample(); s.Goals = null;
        var l = Verification.For(s);
        Check(l.Count == 5, "sin metas debe haber 5 consultas, hay " + l.Count);
        Check(withGoals.Count == 7, "con metas 7");
    }
}
