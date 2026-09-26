using System;
using System.Collections.Generic;
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

        /// <summary>Devuelve la lista de problemas (vacia = valida). Protege contra SQL inyectado por la IA.</summary>
        public List<string> Validate()
        {
            var e = new List<string>();
            Action<string, string> id = (v, what) => { if (v == null || !Ident.IsMatch(v)) e.Add("Identificador invalido en " + what + ": '" + v + "'"); };
            Action<string, string> ty = (v, what) => { if (v == null || !SqlType.IsMatch(v)) e.Add("Tipo SQL no permitido en " + what + ": '" + v + "'"); };
            Action<ColumnMap, string> cm = (c, what) =>
            {
                if (c == null) { e.Add("Falta " + what); return; }
                if (c.Src == null || !SrcIdent.IsMatch(c.Src)) e.Add("Columna origen invalida en " + what + ": '" + c.Src + "'");
                id(c.Dst, what + ".Dst"); ty(c.Type, what + ".Type");
            };

            id(DwName, "DwName");
            if (string.IsNullOrWhiteSpace(SqlServer)) e.Add("Falta SqlServer");
            if (string.IsNullOrWhiteSpace(SourceDsn)) e.Add("Falta SourceDsn");
            if (string.IsNullOrWhiteSpace(SourceQuery) || !SourceQuery.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) e.Add("SourceQuery debe ser un SELECT");
            if (SourceQuery != null && SourceQuery.Contains(";")) e.Add("SourceQuery no debe contener ';'");
            if (Time == null) e.Add("Falta Time"); else
            {
                id(Time.Name, "Time.Name");
                DateTime a, b;
                if (!DateTime.TryParse(Time.From, out a) || !DateTime.TryParse(Time.To, out b) || b < a) e.Add("Rango de Time invalido");
            }
            if (Dims == null || Dims.Count == 0) e.Add("Debe haber al menos una dimension");
            else foreach (var d in Dims)
            {
                id(d.Name, "Dim.Name"); id(d.Key, "Dim.Key");
                if (d.Columns == null || d.Columns.Count == 0) e.Add("La dimension " + d.Name + " no tiene columnas");
                else foreach (var c in d.Columns) { cm(c, "Dim " + d.Name); if (c != null && c.Type != null && !c.Type.StartsWith("VARCHAR", StringComparison.OrdinalIgnoreCase)) e.Add("Las columnas de dimension deben ser VARCHAR (" + d.Name + "." + c.Dst + ")"); }
            }
            if (Fact == null) e.Add("Falta Fact"); else
            {
                id(Fact.Name, "Fact.Name");
                if (Fact.DateColumn == null || !SrcIdent.IsMatch(Fact.DateColumn)) e.Add("Fact.DateColumn invalida");
                cm(Fact.BusinessKey, "Fact.BusinessKey");
                if (Fact.BusinessKey != null && Fact.BusinessKey.Type != null && !Fact.BusinessKey.Type.StartsWith("VARCHAR", StringComparison.OrdinalIgnoreCase)) e.Add("Fact.BusinessKey debe ser VARCHAR");
                if (Fact.Measures == null || Fact.Measures.Count == 0) e.Add("La tabla de hechos necesita medidas");
                else foreach (var m in Fact.Measures) cm(m, "Medida");
                if (Fact.Computed != null) foreach (var c in Fact.Computed)
                {
                    id(c.Name, "Computed.Name");
                    if (string.IsNullOrEmpty(c.Expr) || !SafeExpr.IsMatch(c.Expr)) e.Add("Expresion calculada no permitida en " + c.Name);
                }
            }
            if (Goals != null)
            {
                id(Goals.Table, "Goals.Table");
                if (string.IsNullOrWhiteSpace(Goals.ExcelPath)) e.Add("Goals.ExcelPath vacio");
                if (string.IsNullOrWhiteSpace(Goals.Sheet)) e.Add("Goals.Sheet vacio");
                if (Dims != null && !Dims.Any(d => d.Name == Goals.DimName)) e.Add("Goals.DimName no existe en Dims");
                foreach (var c in new[] { Goals.ExcelDimColumn, Goals.ExcelDateColumn, Goals.ExcelMeasureColumn })
                    if (string.IsNullOrWhiteSpace(c)) e.Add("Faltan columnas del Excel en Goals");
            }
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
