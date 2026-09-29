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

    static DataTable mk(params object[][] rows)
    {
        var t = new DataTable(); foreach (var c in new[] { "p_y", "p_m", "n_rows", "sum_precio_lista", "sum_costo_fabricacion", "sum_extras_instalados", "sum_comision_vendedor" }) t.Columns.Add(c, typeof(object));
        foreach (var r in rows) t.Rows.Add(r); return t;
    }

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
        Test("el ejemplo con todas las opciones valida", () =>
        {
            var s = DwSpec.FromJson(File.ReadAllText(Path.Combine(Root(), "samples", "autos_incremental.json")));
            Check(s.Validate().Count == 0, string.Join("; ", s.Validate()));
            Check(s.IsIncremental && s.RejectsRows && s.UsesUnknownMember && s.Dims[1].HasNaturalKey, "opciones cargadas");
            Check(DdlGenerator.Tables(s).Length > 0 && DdlGenerator.Publish(s).Length > 0 && DdlGenerator.StagingPrepare(s).Length > 0, "genera SQL");
        });
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

        // ---------------- carga segura: staging y publicacion ----------------
        Test("staging: crea stg y las tablas de paso sin FKs", () =>
        {
            var sql = DdlGenerator.StagingPrepare(Sample());
            Check(sql.Contains("CREATE SCHEMA stg") && sql.Contains("CREATE TABLE stg.Dim_Sucursal") && sql.Contains("CREATE TABLE stg.Fact_VentasAutos") && sql.Contains("CREATE TABLE stg.Fact_MetasVentas"), "tablas");
            Check(!sql.Contains("FOREIGN KEY") && !sql.Contains("PERSISTED"), "sin FKs ni calculadas");
            Check(!sql.Contains("dbo."), "no toca dbo");
            Check(sql.IndexOf("DROP TABLE stg.Fact_VentasAutos") < sql.IndexOf("DROP TABLE stg.Dim_Sucursal"), "borra la Fact antes que las dimensiones");
        });
        Test("publicar: una transaccion con rollback, conserva las llaves de las dimensiones", () =>
        {
            var sql = DdlGenerator.Publish(Sample());
            Check(sql.Contains("BEGIN TRANSACTION") && sql.Contains("COMMIT TRANSACTION") && sql.Contains("ROLLBACK TRANSACTION") && sql.Contains("THROW;") && sql.Contains("XACT_ABORT ON"), "transaccion");
            Check(sql.Contains("SET IDENTITY_INSERT dbo.Dim_Sucursal ON") && sql.Contains("FROM stg.Dim_Sucursal"), "identity insert");
            Check(sql.IndexOf("DELETE FROM dbo.Fact_VentasAutos") < sql.IndexOf("DELETE FROM dbo.Dim_Sucursal"), "borra hechos antes que dimensiones");
            Check(sql.IndexOf("INSERT INTO dbo.Dim_Vehiculo") < sql.IndexOf("INSERT INTO dbo.Fact_VentasAutos"), "inserta dimensiones antes que hechos");
            Check(sql.IndexOf("INSERT INTO dbo.Fact_VentasAutos") < sql.IndexOf("INSERT INTO dbo.Fact_MetasVentas"), "metas al final");
            Check(!sql.Contains("Ingreso_Total"), "las columnas calculadas las calcula SQL Server");
            Check(sql.LastIndexOf("END CATCH;") < sql.IndexOf("DROP TABLE stg.Fact_VentasAutos"), "solo borra el staging si publico bien");
            Check(!sql.Contains("ETL_Auditoria"), "no toca la auditoria");
        });

        // ---------------- Unicode ----------------
        Test("NVARCHAR: se acepta, con limite 4000, y llega al DDL", () =>
        {
            var s = Sample(); s.Dims[1].Columns[1].Type = "NVARCHAR(80)";
            Check(s.Validate().Count == 0, string.Join("; ", s.Validate()));
            Check(DdlGenerator.Tables(s).Contains("Marca NVARCHAR(80) NOT NULL"), "DDL");
            Check(DwSpec.IsUnicode("nvarchar(80)") && !DwSpec.IsUnicode("VARCHAR(80)"), "IsUnicode");
            s = Sample(); s.Dims[1].Columns[1].Type = "NVARCHAR(5000)"; Check(Has(s.Validate(), "fuera de rango"), "limite");
            s = Sample(); s.Dims[1].Columns[1].Type = "NVARCHAR(MAX)"; Check(Has(s.Validate(), "Tipo SQL"), "MAX");
            s = Sample(); s.Fact.BusinessKey.Type = "NVARCHAR(40)"; Check(s.Validate().Count == 0, "llave de negocio Unicode");
        });

        // ---------------- llave natural ----------------
        Test("llave natural: valida, restringe el lookup y agrega UNIQUE", () =>
        {
            var s = Sample(); s.Dims[1].NaturalKey = new List<string> { "vin_corto" };
            Check(s.Validate().Count == 0, string.Join("; ", s.Validate()));
            Check(s.Dims[1].LookupColumns().Count == 1 && s.Dims[1].LookupColumns()[0].Dst == "VIN_Corto", "lookup solo por la llave");
            Check(Sample().Dims[1].LookupColumns().Count == 4, "sin llave natural busca por todas");
            Check(DdlGenerator.Tables(s).Contains("UNIQUE (VIN_Corto)"), "UNIQUE en el DDL");
            Check(DdlGenerator.StagingPrepare(s).Contains("UNIQUE (VIN_Corto)"), "UNIQUE en staging");
            s.Dims[1].NaturalKey = new List<string> { "no_existe" }; Check(Has(s.Validate(), "no es una columna"), "columna inexistente");
            s = Sample(); s.Dims[1].NaturalKey = new List<string> { "marca", "MARCA" }; Check(Has(s.Validate(), "Nombre repetido"), "repetida");
        });
        Test("llave natural: la dimension de metas necesita una sola columna", () =>
        {
            var s = Sample(); s.Dims[0].NaturalKey = new List<string> { "sucursal" }; Check(s.Validate().Count == 0, "una columna");
            s = Sample(); s.Goals.DimName = "Dim_Vehiculo"; s.Dims[1].NaturalKey = new List<string> { "marca", "modelo" };
            Check(Has(s.Validate(), "una sola columna"), "dos columnas");
        });
        Test("llave natural: el indice unico respeta el limite de 900 bytes", () =>
        {
            var s = Sample(); s.Dims[1].Columns[0].Type = "NVARCHAR(400)"; s.Dims[1].Columns[1].Type = "NVARCHAR(100)";
            s.Dims[1].NaturalKey = new List<string> { "vin_corto", "marca" }; Check(Has(s.Validate(), "900"), "limite");
        });

        // ---------------- politica de nulos ----------------
        Test("NullPolicy: solo Fail o Unknown", () =>
        {
            var s = Sample(); s.NullPolicy = "Ignorar"; Check(Has(s.Validate(), "NullPolicy"), "invalida");
            s = Sample(); s.NullPolicy = "unknown"; Check(s.Validate().Count == 0, "insensible a mayusculas");
            s = Sample(); s.NullPolicy = "Unknown"; s.Dims[0].Columns[0].Type = "VARCHAR(5)"; Check(Has(s.Validate(), "al menos"), "longitud minima para 'Desconocido'");
        });
        Test("NullPolicy Unknown: el origen se lee con COALESCE y los nulos de dimension no detienen la carga", () =>
        {
            var s = Sample(); s.NullPolicy = "Unknown";
            var q = s.EffectiveSource();
            Check(q.Contains("COALESCE(q.marca, 'Desconocido') AS marca") && q.Contains("q.id_venta") && q.Contains("q.precio_lista") && !q.Contains("COALESCE(q.precio_lista"), "coalesce solo en dimensiones: " + q);
            Check(Sample().EffectiveSource() == Sample().SourceQuery, "con Fail el origen no se toca");
            var p = GoodProfile(s); p["null_marca"] = 12L;
            Check(Reconciliation.Preflight(s, p).Count == 0, "un nulo de dimension se acepta");
            p["null_precio_lista"] = 3L; Check(Has(Reconciliation.Preflight(s, p), "precio_lista"), "un nulo en una medida sigue deteniendo");
            p = GoodProfile(s); p["null_fecha_venta"] = 1L; Check(Has(Reconciliation.Preflight(s, p), "fecha_venta"), "y en la fecha");
        });

        // ---------------- regresion: datos con defecto (P0 #4) ----------------
        Test("regresion: una fila sin dimension o con un nulo hace fallar con el mensaje esperado", () =>
        {
            var s = Sample();
            var conNulo = GoodProfile(s); conNulo["null_sucursal"] = 1L;
            var e = Reconciliation.Preflight(s, conNulo);
            Check(e.Count == 1 && e[0].Contains("'sucursal'") && e[0].Contains("1 valores nulos") && e[0].Contains("NOT NULL"), "nulo: " + string.Join("; ", e));
            var dst = GoodDest(s); dst["n_rows"] = 29L;   // una fila cayo por no encontrar su dimension
            var r = Reconciliation.Compare(s, GoodProfile(s), dst);
            Check(!r.Ok && r.FailureText().Contains("Filas de Fact_VentasAutos: origen 30, destino 29"), r.FailureText());
        });

        // ---------------- prueba de humo del paquete ----------------
        Test("inspector: un paquete conforme pasa y uno con defectos falla", () =>
        {
            var s = Sample();
            var ok = PackageInspector.Verify(s, PackageInspector.Read(FakePackage(s, "0", "0", true)));
            Check(ok.Count == 0, string.Join("; ", ok));
            var bad = PackageInspector.Verify(s, PackageInspector.Read(FakePackage(s, "1", "1", false)));
            Check(Has(bad, "ProtectionLevel") && Has(bad, "NoMatchBehavior=1") && Has(bad, "Falta la columna derivada"), string.Join("; ", bad));
            var noOrder = PackageInspector.Read(FakePackage(s, "0", "0", true).Replace("SQL_Publicar", "SQL_Otra"));
            Check(Has(PackageInspector.Verify(s, noOrder), "Orden de tareas"), "orden");
        });
        Test("inspector: el lookup de metas puede redirigir, solo los de la Fact deben fallar", () =>
        {
            var s = Sample();
            var info = PackageInspector.Read(FakePackage(s, "0", "0", true));
            Check(info.LookupBehavior["DFT_Cargar_Metas_Excel|LK_Sucursal"] == "1", "metas redirige");
            Check(PackageInspector.Verify(s, info).Count == 0, "y aun asi es conforme");
        });

        Test("inspector: el orden incluye la fusion de dimensiones y con Reject no exige lookups", () =>
        {
            var s = Sample(); s.RejectPolicy = "Reject";
            Check(PackageInspector.ExpectedOrder(s).SequenceEqual(new[] { "SQL_Auditoria_Inicio", "SQL_Preparar_Staging", "DFT_Poblar_Dimensiones", "SQL_Fusionar_Dimensiones", "DFT_Extraer_Origen", "DFT_Cargar_Metas_Excel", "SQL_Publicar", "SQL_Auditoria_Fin" }), "orden");
            var info = PackageInspector.Read(FakePackage(s, "0", "1", true));
            Check(PackageInspector.Verify(s, info).Count == 0, "sin lookups en la Fact no hay NoMatchBehavior que exigir: " + string.Join("; ", PackageInspector.Verify(s, info)));
        });

        // ---------------- registro y avisos ----------------
        Test("registro: escribe, rota y oculta secretos", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "etlstudio-log-" + Guid.NewGuid().ToString("N"));
            try
            {
                AppLog.LogDirectory = dir;
                AppLog.Info("conectando con PWD=secreto123;Dsn=x token: dapi0123456789abcdef");
                var txt = File.ReadAllText(AppLog.FilePath);
                Check(txt.Contains("INFO ") && txt.Contains("conectando") && !txt.Contains("secreto123") && !txt.Contains("dapi0123"), "secretos: " + txt);
                File.WriteAllText(AppLog.FilePath, new string('x', (int)AppLog.MaxBytes + 10));
                AppLog.Info("despues de rotar");
                Check(File.Exists(AppLog.FilePath + ".1") && File.ReadAllText(AppLog.FilePath).Contains("despues de rotar") && new FileInfo(AppLog.FilePath).Length < 1000, "rotacion");
                for (int i = 0; i < 8; i++) { File.WriteAllText(AppLog.FilePath, new string('x', (int)AppLog.MaxBytes + 10)); AppLog.Info("n" + i); }
                Check(Directory.GetFiles(dir).Length <= AppLog.Keep + 1, "conserva un maximo de archivos: " + Directory.GetFiles(dir).Length);
            }
            finally { AppLog.LogDirectory = null; try { Directory.Delete(dir, true); } catch { } }
        });
        Test("notificacion: JSON escapado y solo HTTPS", () =>
        {
            var j = Notifier.Json("DW_X", false, "linea1\nlinea \"2\" \\ fin", "C:\\r.docx");
            Check(j.Contains("\"ok\":false") && j.Contains("FALLO - DW_X") && j.Contains("\\n") && j.Contains("\\\"2\\\""), j);
            Check(new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(j)["dw"].ToString() == "DW_X", "JSON valido");
            Check(Notifier.IsValidUrl("https://example.com/hook") && !Notifier.IsValidUrl("http://example.com/hook") && !Notifier.IsValidUrl("no es url"), "URL");
            Check(Notifier.Send("http://x", "d", true, "m", null) != null, "rechaza http");
        });
        Test("requisitos: el formato senala lo que falta y como resolverlo", () =>
        {
            var t = Requirements.Format(new List<Requirement> { new Requirement { Name = "A", Ok = true, Detail = "ok" }, new Requirement { Name = "B", Ok = false, Detail = "no", Fix = "instala B" } });
            Check(t.Contains("[OK]  A") && t.Contains("[FALTA] B") && t.Contains("-> instala B"), t);
        });

        // ---------------- carga incremental ----------------
        Test("incremental: solo el filtro por marca de agua y agrega, no borra", () =>
        {
            var s = Sample(); s.LoadMode = "Incremental";
            Check(s.Validate().Count == 0, string.Join("; ", s.Validate()));
            Check(s.EffectiveSource() == s.SourceQuery, "sin marca de agua se lee todo");
            s.Watermark = new DateTime(2024, 6, 30);
            var q = s.EffectiveSource();
            Check(q.EndsWith("WHERE q.fecha_venta >= CAST('2024-06-30' AS DATE)") && q.Contains("FROM (SELECT id_venta"), "filtro portable: " + q);
            var pub = DdlGenerator.Publish(s);
            Check(!pub.Contains("DELETE FROM dbo.Fact_VentasAutos") && !pub.Contains("DELETE FROM dbo.Dim_"), "no vacia hechos ni dimensiones");
            Check(pub.Contains("DELETE FROM dbo.Fact_MetasVentas"), "las metas del Excel se reemplazan completas");
            Check(pub.Contains("WHERE NOT EXISTS (SELECT 1 FROM dbo.Dim_Sucursal x WHERE x.SK_Sucursal = s.SK_Sucursal)"), "dimensiones: solo llaves nuevas");
            Check(pub.Contains("WHERE NOT EXISTS (SELECT 1 FROM dbo.Fact_VentasAutos x WHERE x.ID_Venta_Origen = j.ID_Venta_Origen)"), "hechos: se reconocen por su llave de negocio");
            var prep = DdlGenerator.StagingPrepare(s);
            Check(prep.Contains("INSERT INTO stg.Dim_Sucursal (SK_Sucursal, Nombre_Sucursal) SELECT SK_Sucursal, Nombre_Sucursal FROM dbo.Dim_Sucursal"), "stg parte de las dimensiones existentes");
            Check(!DdlGenerator.StagingPrepare(Sample()).Contains("FROM dbo.Dim_Sucursal"), "en Full no");
            s.LoadMode = "Diario"; Check(Has(s.Validate(), "LoadMode"), "modo invalido");
        });
        Test("dimensiones: los nuevos miembros se fusionan por llave natural", () =>
        {
            var s = Sample(); s.Dims[1].NaturalKey = new List<string> { "vin_corto" };
            var m = DdlGenerator.MergeDimensions(s);
            Check(m.Contains("INSERT INTO stg.Dim_Vehiculo (VIN_Corto, Marca, Modelo, Tipo_Motor) SELECT DISTINCT n.VIN_Corto, n.Marca, n.Modelo, n.Tipo_Motor FROM stg.Dim_Vehiculo_Nuevos n WHERE NOT EXISTS (SELECT 1 FROM stg.Dim_Vehiculo x WHERE x.VIN_Corto = n.VIN_Corto);"), m);
            Check(DdlGenerator.StagingPrepare(s).Contains("CREATE TABLE stg.Dim_Sucursal_Nuevos"), "tabla de aterrizaje");
            s = Sample(); s.Dims[0].Name = "Dim_Vehiculo_Nuevos"; s.Goals.DimName = "Dim_Vehiculo_Nuevos"; s.Dims[1].Name = "Dim_Vehiculo";
            Check(Has(s.Validate(), "tablas de paso"), "choque de nombres en stg");
        });

        // ---------------- rechazos ----------------
        Test("rechazos: Fact cruda en stg, cruce al publicar y tabla ETL_Rechazos", () =>
        {
            var s = Sample(); s.RejectPolicy = "Reject";
            Check(s.Validate().Count == 0, string.Join("; ", s.Validate()));
            var raw = DdlGenerator.RawColumns(s);
            Check(raw.Count == 1 + 1 + 4 + 4 && raw.All(c => c.Name.StartsWith("s_")) && raw.Count(c => c.IsMeasure) == 4, "columnas crudas: " + raw.Count);
            var prep = DdlGenerator.StagingPrepare(s);
            Check(prep.Contains("CREATE TABLE stg.Fact_VentasAutos (SK_Tiempo INT NOT NULL, s_sucursal VARCHAR(100) NOT NULL") && !prep.Contains("CREATE TABLE stg.Fact_VentasAutos (SK_Tiempo INT NOT NULL, SK_Sucursal"), "stg cruda");
            Check(DdlGenerator.Tables(s).Contains("CREATE TABLE dbo.ETL_Rechazos") && DdlGenerator.Tables(s).Contains("Motivo VARCHAR(200) NOT NULL"), "tabla de rechazos");
            var pub = DdlGenerator.Publish(s);
            Check(pub.Contains("OUTER APPLY (SELECT TOP 1 x.SK_Sucursal AS k FROM stg.Dim_Sucursal x WHERE x.Nombre_Sucursal = r.s_sucursal") && pub.Contains("INTO #j"), "cruce sin multiplicar filas");
            Check(pub.Contains("'Sin dimension Dim_Vehiculo'") && pub.Contains("'Fecha fuera de Dim_Tiempo'"), "motivos");
            Check(pub.Contains("INSERT INTO dbo.ETL_Rechazos") && pub.Contains("WHERE NOT (j.ok_t IS NOT NULL AND j.k_1 IS NOT NULL AND j.k_2 IS NOT NULL)"), "rechazos");
            Check(pub.Contains("DELETE FROM dbo.ETL_Rechazos") && pub.IndexOf("DROP TABLE #j") < pub.IndexOf("COMMIT TRANSACTION"), "Full vacia rechazos; #j se libera dentro de la transaccion");
            Check(!DdlGenerator.Publish(Sample()).Contains("ETL_Rechazos"), "con Fail no hay rechazos");
            s.RejectPolicy = "Otra"; Check(Has(s.Validate(), "RejectPolicy"), "invalida");
            s = Sample(); s.RejectPolicy = "Reject"; s.Fact.Measures[0].Src = "marca"; Check(Has(s.Validate(), "no puede ser tambien"), "medida repetida");
        });
        Test("rechazos: la reconciliacion suma lo rechazado y avisa; las fechas fuera de rango ya no detienen", () =>
        {
            var s = Sample(); s.RejectPolicy = "Reject";
            var d = GoodDest(s); d["n_rows"] = 28L; d["n_bk"] = 28L; d["rej_rows"] = 2L; d["rej_bk"] = 2L;
            foreach (var m in s.Fact.Measures) { d["sum_" + m.Src] = 900.50m; d["rej_sum_" + m.Src] = 100m; }
            var r = Reconciliation.Compare(s, GoodProfile(s), d);
            Check(r.Ok, r.FailureText());
            Check(r.Lines.Any(l => l.Status == "AVISO" && l.Metric.Contains("rechazadas")), "aviso de rechazadas");
            d["rej_rows"] = 1L; Check(!Reconciliation.Compare(s, GoodProfile(s), d).Ok, "si faltan filas sigue fallando");
            var p = GoodProfile(s); p["min_date"] = new DateTime(2019, 1, 1);
            Check(Reconciliation.Preflight(s, p).Count == 0, "en Reject la fecha fuera de rango se rechaza, no detiene");
            Check(Reconciliation.DestSql(s).Contains("(SELECT COUNT_BIG(*) FROM dbo.ETL_Rechazos) AS rej_rows") && !Reconciliation.DestSql(Sample()).Contains("rej_"), "SQL del destino");
            d = GoodDest(s); d["rej_min_date"] = new DateTime(2024, 1, 5); d["min_date"] = new DateTime(2024, 3, 1);
            Check(Reconciliation.Compare(s, GoodProfile(s), d).Ok, "la fecha minima puede estar entre los rechazados");
        });

        // ---------------- reconciliacion por periodo ----------------
        Test("periodos: detecta errores que se compensan en el total", () =>
        {
            var s = Sample();
            Func<int, int, long, decimal, object[]> row = (y, m, n, v) => new object[] { y, m, n, v, v, v, v };
            var src = mk(row(2024, 1, 10, 100m), row(2024, 2, 20, 200m));
            Check(Reconciliation.ComparePeriods(s, src, mk(row(2024, 1, 10, 100m), row(2024, 2, 20, 200m))).All(l => l.Status == "OK"), "iguales");
            // mismas filas y mismo total (300), pero una fila se cargo en el mes equivocado
            var malo = Reconciliation.ComparePeriods(s, src, mk(row(2024, 1, 11, 110m), row(2024, 2, 19, 190m)));
            Check(malo.Count(l => l.Status == "DIFERENCIA") == 3 && malo.Any(l => l.Metric == "Periodo 2024-01") && malo.Any(l => l.Metric == "Periodo 2024-02"), "periodos distintos: " + string.Join("; ", malo.Select(l => l.Metric)));
            Check(Reconciliation.ComparePeriods(s, src, mk(row(2024, 1, 10, 100m))).Any(l => l.Metric == "Periodo 2024-02"), "periodo faltante en el destino");
            Check(Reconciliation.ComparePeriods(s, src, mk(row(2024, 1, 10, 100.004m), row(2024, 2, 20, 200m))).All(l => l.Status == "OK"), "tolera el redondeo");
        });
        Test("periodos: SQL portable y con rechazos", () =>
        {
            var q = Reconciliation.PeriodSourceSql(Sample());
            Check(q.Contains("YEAR(s.fecha_venta) AS p_y") && q.Contains("GROUP BY YEAR(s.fecha_venta), MONTH(s.fecha_venta)") && !q.Contains("LIMIT") && !q.Contains("TOP"), q);
            Check(!Reconciliation.PeriodDestSql(Sample()).Contains("UNION ALL"), "sin rechazos");
            var s = Sample(); s.RejectPolicy = "Reject";
            var d = Reconciliation.PeriodDestSql(s);
            Check(d.Contains("UNION ALL SELECT r.SK_Tiempo / 10000, (r.SK_Tiempo / 100) % 100") && d.Contains("GROUP BY p_y, p_m"), d);
        });

        // ---------------- atributos derivados de dimension ----------------
        Test("dimension: atributos derivados persistidos", () =>
        {
            var s = Sample(); s.Dims[1].Computed.Add(new ComputedSpec { Name = "Inicial_Marca", Expr = "UPPER(LEFT(Marca, 1))" });
            s.Dims[1].Computed.Add(new ComputedSpec { Name = "Segmento", Expr = "CASE WHEN Tipo_Motor = 'Electrico' THEN 'Verde' ELSE 'Otros' END" });
            Check(s.Validate().Count == 0, string.Join("; ", s.Validate()));
            var sql = DdlGenerator.Tables(s);
            Check(sql.Contains("Inicial_Marca AS UPPER(LEFT(Marca, 1)) PERSISTED,") && sql.Contains("Segmento AS CASE WHEN Tipo_Motor = 'Electrico' THEN 'Verde' ELSE 'Otros' END PERSISTED,"), "DDL");
            Check(!DdlGenerator.StagingPrepare(s).Contains("Inicial_Marca") && !DdlGenerator.Publish(s).Contains("Inicial_Marca"), "staging y publicacion no las tocan: las calcula SQL Server");
            s.Dims[1].Computed[0].Expr = "Marca + (SELECT 1)"; Check(Has(s.Validate(), "SELECT"), "subconsulta");
            s.Dims[1].Computed[0].Expr = "LEFT(Otra, 1)"; Check(Has(s.Validate(), "Otra"), "columna ajena");
            s.Dims[1].Computed[0].Expr = "'a' + 'b"; Check(Has(s.Validate(), "Comillas"), "comillas");
            s.Dims[1].Computed[0].Expr = "x; DROP TABLE t"; Check(s.Validate().Count > 0, "inyeccion");
            s = Sample(); s.Dims[1].Computed.Add(new ComputedSpec { Name = "Marca", Expr = "LEFT(Modelo, 1)" }); Check(Has(s.Validate(), "repite un nombre"), "nombre repetido");
            s = Sample(); s.Dims[1].Computed.Add(new ComputedSpec { Name = "Fecha_Creacion", Expr = "LEFT(Modelo, 1)" }); Check(Has(s.Validate(), "reservado"), "reservado");
        });

        // ---------------- origen ----------------
        Test("origen: motor, vista previa y textos", () =>
        {
            Check(DwSpec.PreviewSql("Databricks", "SELECT 1 AS a", 20) == "SELECT * FROM (SELECT 1 AS a) AS q LIMIT 20", "LIMIT");
            Check(DwSpec.PreviewSql("PostgreSQL", "SELECT 1 AS a", 5).EndsWith("LIMIT 5"), "postgres");
            Check(DwSpec.PreviewSql("SqlServer", "SELECT 1 AS a", 5) == "SELECT TOP 5 * FROM (SELECT 1 AS a) AS q", "TOP");
            var s = Sample(); Check(s.SourceKind == "Databricks" && s.SourceLabel.Contains("Databricks"), "por defecto");
            s.SourceKind = "PostgreSQL"; Check(s.Validate().Count == 0 && s.SourceLabel.Contains("PostgreSQL"), "postgres valido");
            s.SourceKind = "Oracle"; Check(Has(s.Validate(), "SourceKind"), "desconocido");
            Check(DwSpec.FromJson(Sample().ToJson()).SourceKind == "Databricks", "ida y vuelta");
        });
        Test("las opciones nuevas sobreviven a JSON y la marca de agua no se guarda", () =>
        {
            var s = Sample(); s.LoadMode = "Incremental"; s.RejectPolicy = "Reject"; s.NullPolicy = "Unknown"; s.Watermark = new DateTime(2024, 1, 1);
            s.Dims[1].Computed.Add(new ComputedSpec { Name = "Ini", Expr = "LEFT(Marca, 1)" });
            var j = s.ToJson(); var b = DwSpec.FromJson(j);
            Check(b.IsIncremental && b.RejectsRows && b.UsesUnknownMember && b.Dims[1].Computed.Count == 1, "opciones");
            Check(!j.Contains("Watermark") && !b.Watermark.HasValue, "la marca de agua es de ejecucion");
        });

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "OK: " + passed + " comprobaciones correctas." : "FALLARON " + failed + " de " + (passed + failed) + " comprobaciones.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Paquete .dtsx minimo con la estructura que EtlStudio genera (segun el formato de SSIS 2012+; no es un paquete real).</summary>
    static string FakePackage(DwSpec s, string protection, string factNoMatch, bool withSk)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><DTS:Executable xmlns:DTS=\"www.microsoft.com/SqlServer/Dts\" DTS:ExecutableType=\"Microsoft.Package\" DTS:ProtectionLevel=\"" + protection + "\"><DTS:Executables>");
        foreach (var n in PackageInspector.ExpectedOrder(s))
        {
            sb.Append("<DTS:Executable DTS:ObjectName=\"" + n + "\">");
            if (n == "DFT_Extraer_Origen")
            {
                foreach (var d in s.Dims) sb.Append("<component name=\"LK_" + d.Name.Replace("Dim_", "") + "\"><properties><property name=\"NoMatchBehavior\">" + factNoMatch + "</property></properties></component>");
                if (withSk) sb.Append("<component name=\"DC_SK_Tiempo\"><properties><property name=\"FriendlyExpression\">(DT_I4)(YEAR((DT_DBTIMESTAMP)fecha_venta) * 10000 + MONTH((DT_DBTIMESTAMP)fecha_venta) * 100 + DAY((DT_DBTIMESTAMP)fecha_venta))</property></properties></component>");
            }
            if (n == "DFT_Cargar_Metas_Excel")
                sb.Append("<component name=\"LK_Sucursal\"><properties><property name=\"NoMatchBehavior\">1</property></properties></component>");
            sb.Append("</DTS:Executable>");
        }
        sb.Append("</DTS:Executables><DTS:PrecedenceConstraints>");
        var o = PackageInspector.ExpectedOrder(s);
        for (int i = 1; i < o.Count; i++) sb.Append("<DTS:PrecedenceConstraint DTS:From=\"Package\\" + o[i - 1] + "\" DTS:To=\"Package\\" + o[i] + "\"/>");
        sb.Append("</DTS:PrecedenceConstraints></DTS:Executable>");
        return sb.ToString();
    }

    static void s_noGoals(List<Check> withGoals)
    {
        var s = Sample(); s.Goals = null;
        var l = Verification.For(s);
        Check(l.Count == 5, "sin metas debe haber 5 consultas, hay " + l.Count);
        Check(withGoals.Count == 7, "con metas 7");
    }
}
