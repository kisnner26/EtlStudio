using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace EtlStudio.Core
{
    /// <summary>Columna origen -> columna destino, con tipo SQL Server.</summary>
    public class ColumnMap
    {
        public string Src { get; set; }
        public string Dst { get; set; }
        public string Type { get; set; }
    }

    public class DimSpec
    {
        public string Name { get; set; }
        public string Key { get; set; }
        public List<ColumnMap> Columns { get; set; } = new List<ColumnMap>();
    }

    public class ComputedSpec
    {
        public string Name { get; set; }
        public string Expr { get; set; }
    }

    public class FactSpec
    {
        public string Name { get; set; }
        public string DateColumn { get; set; }
        public ColumnMap BusinessKey { get; set; }
        public List<ColumnMap> Measures { get; set; } = new List<ColumnMap>();
        public List<ComputedSpec> Computed { get; set; } = new List<ComputedSpec>();
    }

    public class TimeSpec
    {
        public string Name { get; set; } = "Dim_Tiempo";
        public string From { get; set; } = "2023-01-01";
        public string To { get; set; } = "2030-12-31";
    }

    /// <summary>Metas / presupuestos que llegan en un Excel (opcional).</summary>
    public class GoalsSpec
    {
        public string Table { get; set; }
        public string ExcelPath { get; set; }
        public string Sheet { get; set; }
        public string DimName { get; set; }
        public string ExcelDimColumn { get; set; }
        public string ExcelDateColumn { get; set; }
        public string ExcelMeasureColumn { get; set; }
    }

    /// <summary>Descripcion completa del DW en estrella. La IA la propone; el usuario la revisa.</summary>
    public class DwSpec
    {
        public string DwName { get; set; }
        public string SqlServer { get; set; }
        public string SourceDsn { get; set; }
        public string SourceQuery { get; set; }
        public TimeSpec Time { get; set; } = new TimeSpec();
        public List<DimSpec> Dims { get; set; } = new List<DimSpec>();
        public FactSpec Fact { get; set; } = new FactSpec();
        public GoalsSpec Goals { get; set; }

        static readonly Regex Ident = new Regex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$");
        static readonly Regex SqlType = new Regex(@"^(VARCHAR\(\d{1,4}\)|INT|BIGINT|DECIMAL\(\d{1,2},\d{1,2}\)|DATE|DATETIME|BIT|FLOAT)$", RegexOptions.IgnoreCase);
        static readonly Regex SafeExpr = new Regex(@"^[A-Za-z0-9_ ()+\-*/.,]+$");
        static readonly Regex SrcIdent = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");

        public static DwSpec FromJson(string json)
        {
            var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return s.Deserialize<DwSpec>(json);
        }

        public string ToJson()
        {
            var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return Pretty(s.Serialize(this));
        }

        static string Pretty(string json)
        {
            var sb = new System.Text.StringBuilder();
            int indent = 0; bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inStr = !inStr;
                if (inStr) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[': sb.Append(c).Append('\n').Append(' ', ++indent * 2); break;
                    case '}': case ']': sb.Append('\n').Append(' ', --indent * 2).Append(c); break;
                    case ',': sb.Append(c).Append('\n').Append(' ', indent * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Columnas que el modelo genera o reserva; una columna del usuario no puede llamarse igual.</summary>
        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SK_Tiempo", "SK_Fact", "SK_Meta", "Fecha_Carga", "Fecha_Creacion", "Monto_Meta" };

        /// <summary>Unicas funciones permitidas dentro de una columna calculada.</summary>
        static readonly HashSet<string> ExprFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ISNULL", "NULLIF", "COALESCE", "ROUND", "ABS", "CEILING", "FLOOR" };

        static readonly Regex NumericType = new Regex(@"^(INT|BIGINT|FLOAT|DECIMAL\(\d{1,2},\d{1,2}\))$", RegexOptions.IgnoreCase);
        static readonly Regex ExcelName = new Regex(@"^[\p{L}0-9_ .]{1,64}$");
        static readonly Regex ExcelSheet = new Regex(@"^[\p{L}0-9_ .$]{1,64}$");
        static readonly Regex DsnName = new Regex(@"^[A-Za-z0-9_ .\-]{1,64}$");
        static readonly Regex ExprToken = new Regex(@"[A-Za-z_][A-Za-z0-9_]*");

        public const string DateFormat = "yyyy-MM-dd";
        public const int MaxTimeDays = 36525;   // 100 anios
        public const string AuditTableName = "ETL_Auditoria";

        /// <summary>VARCHAR(1..8000) y DECIMAL(p<=38, s<=p): limites reales de SQL Server y de DT_STR.</summary>
        static bool TypeBounds(string t)
        {
            var m = Regex.Match(t ?? "", @"^(VARCHAR|DECIMAL)\((\d+)(?:,(\d+))?\)$", RegexOptions.IgnoreCase);
            if (!m.Success) return true;
            int a = int.Parse(m.Groups[2].Value);
            if (m.Groups[1].Value.Equals("VARCHAR", StringComparison.OrdinalIgnoreCase)) return a >= 1 && a <= 8000;
            int b = int.Parse(m.Groups[3].Value);
            return a >= 1 && a <= 38 && b <= a;
        }

        /// <summary>Devuelve la lista de problemas (vacia = valida). Protege contra SQL inyectado por la IA y contra disenos que no cargarian bien.</summary>
        public List<string> Validate()
        {
            var e = new List<string>();
            Action<string, string> id = (v, what) => { if (v == null || !Ident.IsMatch(v)) e.Add("Identificador invalido en " + what + ": '" + v + "'"); };
            Action<string, string> ty = (v, what) =>
            {
                if (v == null || !SqlType.IsMatch(v)) e.Add("Tipo SQL no permitido en " + what + ": '" + v + "'");
                else if (!TypeBounds(v)) e.Add("Tipo SQL fuera de rango en " + what + ": '" + v + "'");
            };
            Action<ColumnMap, string> cm = (c, what) =>
            {
                if (c == null) { e.Add("Falta " + what); return; }
                if (c.Src == null || !SrcIdent.IsMatch(c.Src)) e.Add("Columna origen invalida en " + what + ": '" + c.Src + "'");
                id(c.Dst, what + ".Dst"); ty(c.Type, what + ".Type");
                if (c.Dst != null && Reserved.Contains(c.Dst)) e.Add("El nombre '" + c.Dst + "' esta reservado por el modelo (" + what + ")");
            };
            Action<IEnumerable<string>, string> unique = (names, what) =>
            {
                foreach (var g in names.Where(n => n != null).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                    e.Add("Nombre repetido en " + what + ": '" + g.Key + "'");
            };

            id(DwName, "DwName");
            if (string.IsNullOrWhiteSpace(SqlServer)) e.Add("Falta SqlServer");
            else if (SqlServer.IndexOfAny(new[] { ';', '=', '"', '\'' }) >= 0) e.Add("SqlServer contiene caracteres no permitidos (; = comillas)");
            if (string.IsNullOrWhiteSpace(SourceDsn)) e.Add("Falta SourceDsn");
            else if (!DsnName.IsMatch(SourceDsn)) e.Add("SourceDsn contiene caracteres no permitidos: '" + SourceDsn + "'");
            if (string.IsNullOrWhiteSpace(SourceQuery) || !SourceQuery.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) e.Add("SourceQuery debe ser un SELECT");
            if (SourceQuery != null && SourceQuery.Contains(";")) e.Add("SourceQuery no debe contener ';'");
            if (SourceQuery != null && (SourceQuery.Contains("--") || SourceQuery.Contains("/*"))) e.Add("SourceQuery no debe contener comentarios (-- o /* */): se incrusta dentro de una subconsulta");

            if (Time == null) e.Add("Falta Time"); else
            {
                id(Time.Name, "Time.Name");
                DateTime a, b;
                if (!DateTime.TryParseExact(Time.From, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out a) ||
                    !DateTime.TryParseExact(Time.To, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out b) || b < a)
                    e.Add("Rango de Time invalido: usa " + DateFormat + " y Desde <= Hasta");
                else if ((b - a).TotalDays > MaxTimeDays) e.Add("Rango de Time demasiado grande (maximo 100 anios)");
            }

            var tables = new List<string>();
            if (Time != null) tables.Add(Time.Name);
            if (Dims == null || Dims.Count == 0) e.Add("Debe haber al menos una dimension");
            else foreach (var d in Dims)
            {
                id(d.Name, "Dim.Name"); id(d.Key, "Dim.Key"); tables.Add(d.Name);
                if (d.Key != null && Reserved.Contains(d.Key)) e.Add("La llave '" + d.Key + "' esta reservada por el modelo");
                if (d.Columns == null || d.Columns.Count == 0) e.Add("La dimension " + d.Name + " no tiene columnas");
                else
                {
                    foreach (var c in d.Columns)
                    {
                        cm(c, "Dim " + d.Name);
                        if (c != null && c.Type != null && !c.Type.StartsWith("VARCHAR", StringComparison.OrdinalIgnoreCase)) e.Add("Las columnas de dimension deben ser VARCHAR (" + d.Name + "." + c.Dst + ")");
                    }
                    unique(d.Columns.Where(c => c != null).Select(c => c.Dst).Concat(new[] { d.Key }), "la dimension " + d.Name);
                    unique(d.Columns.Where(c => c != null).Select(c => c.Src), "las columnas origen de " + d.Name);
                }
            }
            if (Dims != null) unique(Dims.Select(d => d.Key), "las llaves de las dimensiones");

            if (Fact == null) e.Add("Falta Fact"); else
            {
                id(Fact.Name, "Fact.Name"); tables.Add(Fact.Name);
                if (Fact.DateColumn == null || !SrcIdent.IsMatch(Fact.DateColumn)) e.Add("Fact.DateColumn invalida");
                cm(Fact.BusinessKey, "Fact.BusinessKey");
                if (Fact.BusinessKey != null && Fact.BusinessKey.Type != null && !Fact.BusinessKey.Type.StartsWith("VARCHAR", StringComparison.OrdinalIgnoreCase)) e.Add("Fact.BusinessKey debe ser VARCHAR");
                var measureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Fact.Measures == null || Fact.Measures.Count == 0) e.Add("La tabla de hechos necesita medidas");
                else
                {
                    foreach (var m in Fact.Measures)
                    {
                        cm(m, "Medida");
                        if (m != null && m.Type != null && !NumericType.IsMatch(m.Type)) e.Add("Las medidas deben ser numericas (INT, BIGINT, FLOAT o DECIMAL): " + m.Dst);
                        if (m != null && m.Dst != null) measureNames.Add(m.Dst);
                        if (m != null && m.Src != null && (m.Src.Equals("SK_Tiempo", StringComparison.OrdinalIgnoreCase) || (Dims != null && Dims.Any(d => d.Key != null && d.Key.Equals(m.Src, StringComparison.OrdinalIgnoreCase)))))
                            e.Add("La medida '" + m.Src + "' se llama igual que una llave del modelo");
                    }
                    unique(Fact.Measures.Where(m => m != null).Select(m => m.Src), "las medidas (origen)");
                }
                var factCols = new List<string>();
                if (Fact.BusinessKey != null) factCols.Add(Fact.BusinessKey.Dst);
                if (Fact.Measures != null) factCols.AddRange(Fact.Measures.Where(m => m != null).Select(m => m.Dst));
                if (Fact.Computed != null) factCols.AddRange(Fact.Computed.Where(c => c != null).Select(c => c.Name));
                if (Dims != null) factCols.AddRange(Dims.Where(d => d != null).Select(d => d.Key));
                unique(factCols, "la tabla de hechos " + Fact.Name);
                if (Fact.Computed != null) foreach (var c in Fact.Computed)
                {
                    id(c.Name, "Computed.Name");
                    if (c.Name != null && Reserved.Contains(c.Name)) e.Add("El nombre '" + c.Name + "' esta reservado por el modelo (Computed)");
                    if (string.IsNullOrEmpty(c.Expr) || !SafeExpr.IsMatch(c.Expr)) { e.Add("Expresion calculada no permitida en " + c.Name); continue; }
                    foreach (Match t in ExprToken.Matches(c.Expr))
                        if (!measureNames.Contains(t.Value) && !ExprFunctions.Contains(t.Value))
                            e.Add("La expresion de " + c.Name + " usa '" + t.Value + "', que no es una medida ni una funcion permitida");
                }
            }

            if (Goals != null)
            {
                id(Goals.Table, "Goals.Table"); tables.Add(Goals.Table);
                if (string.IsNullOrWhiteSpace(Goals.ExcelPath)) e.Add("Goals.ExcelPath vacio");
                else if (Goals.ExcelPath.IndexOfAny(new[] { ';', '"' }) >= 0) e.Add("Goals.ExcelPath contiene caracteres no permitidos (; o comillas)");
                if (string.IsNullOrWhiteSpace(Goals.Sheet)) e.Add("Goals.Sheet vacio");
                else if (!ExcelSheet.IsMatch(Goals.Sheet)) e.Add("Goals.Sheet contiene caracteres no permitidos: '" + Goals.Sheet + "'");
                if (Dims != null && !Dims.Any(d => d.Name == Goals.DimName)) e.Add("Goals.DimName no existe en Dims");
                foreach (var c in new[] { Goals.ExcelDimColumn, Goals.ExcelDateColumn, Goals.ExcelMeasureColumn })
                    if (string.IsNullOrWhiteSpace(c)) e.Add("Faltan columnas del Excel en Goals");
                    else if (!ExcelName.IsMatch(c)) e.Add("Columna de Excel con caracteres no permitidos: '" + c + "'");
            }

            tables.Add(AuditTableName);
            unique(tables, "las tablas del DW");
            return e;
        }

        public static int VarcharLen(string type)
        {
            var m = Regex.Match(type ?? "", @"\((\d+)\)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 100;
        }

        /// <summary>Ejemplo del curso: caso_autos en Databricks -> DW_Autos_DBRICKS.</summary>
        public static DwSpec ExampleAutos()
        {
            return new DwSpec
            {
                DwName = "DW_Autos_DBRICKS",
                SqlServer = @"localhost\KISNNER",
                SourceDsn = "Databricks_SIS",
                SourceQuery = "SELECT id_venta, fecha_venta, sucursal, vin_corto, marca, modelo, tipo_motor, precio_lista, costo_fabricacion, extras_instalados, comision_vendedor FROM caso_autos.bronze.ventas_autos_crudo",
                Time = new TimeSpec { Name = "Dim_Tiempo", From = "2023-01-01", To = "2030-12-31" },
                Dims = new List<DimSpec>
                {
                    new DimSpec { Name = "Dim_Sucursal", Key = "SK_Sucursal", Columns = { new ColumnMap { Src = "sucursal", Dst = "Nombre_Sucursal", Type = "VARCHAR(100)" } } },
                    new DimSpec { Name = "Dim_Vehiculo", Key = "SK_Vehiculo", Columns =
                    {
                        new ColumnMap { Src = "vin_corto", Dst = "VIN_Corto", Type = "VARCHAR(50)" },
                        new ColumnMap { Src = "marca", Dst = "Marca", Type = "VARCHAR(50)" },
                        new ColumnMap { Src = "modelo", Dst = "Modelo", Type = "VARCHAR(50)" },
                        new ColumnMap { Src = "tipo_motor", Dst = "Tipo_Motor", Type = "VARCHAR(50)" }
                    } }
                },
                Fact = new FactSpec
                {
                    Name = "Fact_VentasAutos",
                    DateColumn = "fecha_venta",
                    BusinessKey = new ColumnMap { Src = "id_venta", Dst = "ID_Venta_Origen", Type = "VARCHAR(50)" },
                    Measures =
                    {
                        new ColumnMap { Src = "precio_lista", Dst = "Precio_Lista", Type = "DECIMAL(18,2)" },
                        new ColumnMap { Src = "costo_fabricacion", Dst = "Costo_Fabricacion", Type = "DECIMAL(18,2)" },
                        new ColumnMap { Src = "extras_instalados", Dst = "Extras_Instalados", Type = "DECIMAL(18,2)" },
                        new ColumnMap { Src = "comision_vendedor", Dst = "Comision_Vendedor", Type = "DECIMAL(18,2)" }
                    },
                    Computed =
                    {
                        new ComputedSpec { Name = "Ingreso_Total", Expr = "(Precio_Lista + Extras_Instalados)" },
                        new ComputedSpec { Name = "Margen_Bruto", Expr = "((Precio_Lista + Extras_Instalados) - Costo_Fabricacion)" }
                    }
                },
                Goals = new GoalsSpec
                {
                    Table = "Fact_MetasVentas",
                    ExcelPath = @"C:\Users\kisnn\Downloads\downloads\Metas_Ventas_2024.xlsx",
                    Sheet = "Metas_2024$",
                    DimName = "Dim_Sucursal",
                    ExcelDimColumn = "Sucursal",
                    ExcelDateColumn = "Fecha",
                    ExcelMeasureColumn = "Monto_Meta"
                }
            };
        }
    }
}
