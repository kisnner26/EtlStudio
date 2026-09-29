using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Microsoft.SqlServer.Dts.Runtime;
using DT = Microsoft.SqlServer.Dts.Runtime.Wrapper.DataType;

namespace EtlStudio.Core
{
    /// <summary>
    /// Construye un paquete SSIS (.dtsx) real con el modelo de objetos de SSIS (runtime 2022 / v16):
    /// limpieza, DFT de dimensiones, DFT de hechos (Conversion de datos + Lookups + Columna derivada + carga rapida)
    /// y, opcionalmente, DFT de metas desde Excel.
    /// </summary>
    public class SsisBuilder
    {
        const string AdoSrc = "Microsoft.SqlServer.Dts.Pipeline.DataReaderSourceAdapter, Microsoft.SqlServer.ADONETSrc, Version=16.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91";
        const string XlSrc = "{C354904A-51DF-486A-8874-2F561904681D}";
        const string OleDst = "{6FB871A7-7C0F-4421-A691-CF8734E3EBE5}";
        const string DataCnv = "{378AD921-5B32-4299-B927-057D91107F7F}";
        const string LookupId = "{11603DAE-8E2F-4545-A083-45E3B1EF640E}";
        const string DerivedId = "{366C7B66-EC78-4F92-95FE-44FFDA11E184}";

        class Comp { public IDTSComponentMetaData100 Meta; public CManagedComponentWrapper Inst; }

        readonly DwSpec spec;
        Package pkg; ConnectionManager cmSql, cmDbx, cmXls;

        public SsisBuilder(DwSpec spec) { this.spec = spec; }

        public string Build(string outPath)
        {
            var errs = spec.Validate();
            if (errs.Count > 0) throw new InvalidOperationException("Spec invalida:\n - " + string.Join("\n - ", errs));

            var app = new Application();
            pkg = new Package { Name = "Package" };
            pkg.ProtectionLevel = DTSProtectionLevel.DontSaveSensitive;   // no guarda secretos: el token vive en el DSN, y asi abre en cualquier equipo
            pkg.Description = "ETL generado por EtlStudio: " + spec.SourceDsn + " -> " + spec.DwName;

            cmSql = pkg.Connections.Add("OLEDB"); cmSql.Name = spec.DwName;
            cmSql.ConnectionString = "Data Source=" + spec.SqlServer + ";Initial Catalog=" + spec.DwName +
                ";Provider=MSOLEDBSQL;Integrated Security=SSPI;Use Encryption for Data=True;Trust Server Certificate=True;Auto Translate=False;";
            cmDbx = pkg.Connections.Add("ADO.NET:System.Data.Odbc.OdbcConnection, System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
            cmDbx.Name = spec.SourceDsn; cmDbx.ConnectionString = "Dsn=" + spec.SourceDsn + ";";

            var steps = new List<TaskHost>();
            steps.Add(ExecSql("SQL_Auditoria_Inicio", DdlGenerator.AuditStart(spec)));
            steps.Add(ExecSql("SQL_Preparar_Staging", DdlGenerator.StagingPrepare(spec)));   // la carga escribe en stg; dbo no se toca hasta publicar
            steps.Add(BuildDimensionsFlow());
            steps.Add(ExecSql("SQL_Fusionar_Dimensiones", DdlGenerator.MergeDimensions(spec)));   // agrega a stg.Dim_* solo los miembros nuevos
            steps.Add(BuildFactFlow());
            if (spec.Goals != null)
            {
                cmXls = pkg.Connections.Add("EXCEL"); cmXls.Name = "Excel " + System.IO.Path.GetFileNameWithoutExtension(spec.Goals.ExcelPath);
                cmXls.ConnectionString = "Provider=Microsoft.ACE.OLEDB.16.0;Data Source=" + spec.Goals.ExcelPath + ";Extended Properties=\"Excel 12.0;HDR=YES\";";
                steps.Add(BuildGoalsFlow());
            }
            steps.Add(ExecSql("SQL_Publicar", DdlGenerator.Publish(spec)));   // una transaccion: si falla, el destino queda como estaba
            steps.Add(ExecSql("SQL_Auditoria_Fin", DdlGenerator.AuditEnd(spec)));   // solo se alcanza si todo lo anterior termino bien
            for (int i = 1; i < steps.Count; i++) pkg.PrecedenceConstraints.Add((Executable)steps[i - 1], (Executable)steps[i]);

            app.SaveToXml(outPath, pkg, null);
            return outPath;
        }

        // ---------------- flujos ----------------

        TaskHost BuildDimensionsFlow()
        {
            MainPipe p; var th = Flow("DFT_Poblar_Dimensiones", out p);
            int n = 0;
            foreach (var d in spec.Dims)
            {
                string q = DimensionQuery(d);
                string suf = n++ == 0 ? "" : " " + (n - 1);
                Comp src = AdoSource(p, "Origen de ADO NET" + suf, q);
                var conv = new List<object>();
                foreach (var c in d.Columns) { conv.Add(c.Src); conv.Add(c); }
                Comp cv = DataConv(p, "Conversion de datos" + suf, FirstOut(src), conv.ToArray());
                var map = new Dictionary<string, string>();
                foreach (var c in d.Columns) map["Copy of " + c.Src] = c.Dst;
                OleDest(p, "Destino " + d.Name, FirstOut(cv), Stg(d.Name + "_Nuevos"), map, null);
            }
            return th;
        }

        TaskHost BuildFactFlow()
        {
            MainPipe p; var th = Flow("DFT_Extraer_Origen", out p);
            Comp src = AdoSource(p, "Origen de ADO NET", spec.EffectiveSource());

            var conv = new List<object>(); var seen = new HashSet<string>();
            foreach (var d in spec.Dims) foreach (var c in d.Columns) if (seen.Add(c.Src)) { conv.Add(c.Src); conv.Add(c); }
            var bk = spec.Fact.BusinessKey; if (seen.Add(bk.Src)) { conv.Add(bk.Src); conv.Add(bk); }
            Comp last = DataConv(p, "Conversion de datos", FirstOut(src), conv.ToArray());

            IDTSOutput100 outp = FirstOut(last);
            if (!spec.RejectsRows)
                foreach (var d in spec.Dims)
                {
                    var lc = d.LookupColumns();   // la llave natural si se definio (mas robusto ante mayusculas, acentos o collation); si no, todas las columnas
                    Comp lk = Look(p, "LK_" + d.Name.Replace("Dim_", ""), outp, Stg(d.Name),
                        lc.Select(c => "Copy of " + c.Src).ToArray(), lc.Select(c => c.Dst).ToArray(), new[] { d.Key }, true);
                    outp = MatchOut(lk);
                }
            // Con RejectPolicy 'Reject' no hay Lookups en el flujo: la Fact se aterriza cruda en stg y SQL_Publicar la cruza con las dimensiones.
            string dateCol = spec.Fact.DateColumn;
            Comp dt = Derive(p, "DC_SK_Tiempo", outp, "SK_Tiempo",
                "(DT_I4)(YEAR((DT_DBTIMESTAMP)" + dateCol + ") * 10000 + MONTH((DT_DBTIMESTAMP)" + dateCol + ") * 100 + DAY((DT_DBTIMESTAMP)" + dateCol + "))", dateCol);

            var map = new Dictionary<string, string> { { "SK_Tiempo", "SK_Tiempo" } };
            if (spec.RejectsRows)
            {
                foreach (var rc in DdlGenerator.RawColumns(spec)) map[rc.IsMeasure ? rc.Map.Src : "Copy of " + rc.Map.Src] = rc.Name;
            }
            else
            {
                foreach (var d in spec.Dims) map[d.Key] = d.Key;
                map["Copy of " + bk.Src] = bk.Dst;
                foreach (var m in spec.Fact.Measures) map[m.Src] = m.Dst;
            }
            OleDest(p, "Destino " + spec.Fact.Name, FirstOut(dt), Stg(spec.Fact.Name), map, "TABLOCK,CHECK_CONSTRAINTS");
            return th;
        }

        TaskHost BuildGoalsFlow()
        {
            var g = spec.Goals; var gd = spec.Dims.First(d => d.Name == g.DimName); var jc = gd.LookupColumns()[0];
            MainPipe p; var th = Flow("DFT_Cargar_Metas_Excel", out p);
            Comp x = NewComp(p, "Origen de Excel", XlSrc); UseConn(x, cmXls);
            x.Inst.SetComponentProperty("AccessMode", 0); x.Inst.SetComponentProperty("OpenRowset", g.Sheet);
            Refresh(x);
            Comp cx = DataConv(p, "Conversion de datos", FirstOut(x), g.ExcelDimColumn, jc);
            Comp lx = Look(p, "LK_" + gd.Name.Replace("Dim_", ""), FirstOut(cx), Stg(gd.Name),
                new[] { "Copy of " + g.ExcelDimColumn }, new[] { jc.Dst }, new[] { gd.Key }, false);
            string f = g.ExcelDateColumn;
            Comp dx = Derive(p, "DC_SK_Tiempo", MatchOut(lx), "SK_Tiempo",
                "(DT_I4)((DT_WSTR,4)YEAR((DT_DBTIMESTAMP)" + f + ") + RIGHT(\"0\" + (DT_WSTR,2)MONTH((DT_DBTIMESTAMP)" + f + "),2) + \"01\")", f);
            OleDest(p, "Destino " + g.Table, FirstOut(dx), Stg(g.Table),
                new Dictionary<string, string> { { "SK_Tiempo", "SK_Tiempo" }, { gd.Key, gd.Key }, { g.ExcelMeasureColumn, "Monto_Meta" } },
                "TABLOCK,CHECK_CONSTRAINTS");
            return th;
        }

        static string Stg(string table) { return "[" + DdlGenerator.StagingSchema + "].[" + table + "]"; }

        /// <summary>Miembros distintos de una dimension. Con llave natural: una fila por llave (los demas atributos con MAX, portable entre motores).</summary>
        string DimensionQuery(DimSpec d)
        {
            string src = spec.EffectiveSource();
            if (!d.HasNaturalKey)
                return "SELECT DISTINCT " + string.Join(", ", d.Columns.Select(c => c.Src)) + " FROM (" + src + ") AS s WHERE " + d.Columns[0].Src + " IS NOT NULL";
            var nk = d.LookupColumns();
            var sel = d.Columns.Select(c => nk.Contains(c) ? c.Src : "MAX(" + c.Src + ") AS " + c.Src);
            return "SELECT " + string.Join(", ", sel) + " FROM (" + src + ") AS s WHERE " + string.Join(" AND ", nk.Select(c => c.Src + " IS NOT NULL")) +
                   " GROUP BY " + string.Join(", ", nk.Select(c => c.Src));
        }

        // ---------------- helpers de bajo nivel ----------------

        TaskHost ExecSql(string name, string sql)
        {
            var th = (TaskHost)pkg.Executables.Add("STOCK:SQLTask"); th.Name = name;
            th.Properties["Connection"].SetValue(th, cmSql.ID);
            th.Properties["SqlStatementSource"].SetValue(th, sql);
            th.Properties["TimeOut"].SetValue(th, (uint)0);
            return th;
        }

        TaskHost Flow(string name, out MainPipe pipe)
        {
            var th = (TaskHost)pkg.Executables.Add("STOCK:PipelineTask"); th.Name = name; th.DelayValidation = true;
            pipe = (MainPipe)th.InnerObject; return th;
        }

        static Comp NewComp(MainPipe pipe, string name, string cls)
        {
            var c = new Comp { Meta = pipe.ComponentMetaDataCollection.New() };
            c.Meta.ComponentClassID = cls;
            c.Inst = c.Meta.Instantiate(); c.Inst.ProvideComponentProperties();
            c.Meta.Name = name; // despues de ProvideComponentProperties, que reinicia el nombre
            return c;
        }

        static void UseConn(Comp c, ConnectionManager cm)
        {
            c.Meta.RuntimeConnectionCollection[0].ConnectionManagerID = cm.ID;
            c.Meta.RuntimeConnectionCollection[0].ConnectionManager = DtsConvert.GetExtendedInterface(cm);
        }

        static void Refresh(Comp c) { c.Inst.AcquireConnections(null); c.Inst.ReinitializeMetaData(); c.Inst.ReleaseConnections(); }

        static void Connect(MainPipe pipe, IDTSOutput100 o, Comp to)
        {
            pipe.PathCollection.New().AttachPathAndPropagateNotifications(o, to.Meta.InputCollection[0]);
        }

        static IDTSOutput100 FirstOut(Comp c)
        {
            foreach (IDTSOutput100 o in c.Meta.OutputCollection) if (!o.IsErrorOut) return o;
            throw new Exception("Componente sin salida");
        }

        static IDTSOutput100 MatchOut(Comp c)
        {
            foreach (IDTSOutput100 o in c.Meta.OutputCollection) if (o.Name == "Lookup Match Output") return o;
            throw new Exception("No se encontro 'Lookup Match Output'");
        }

        static IDTSVirtualInputColumn100 VC(IDTSVirtualInput100 vi, string name)
        {
            foreach (IDTSVirtualInputColumn100 v in vi.VirtualInputColumnCollection) if (v.Name == name) return v;
            throw new Exception("Columna no encontrada en el flujo: '" + name + "' (revisa que el SELECT origen la devuelva)");
        }

        Comp AdoSource(MainPipe pipe, string name, string query)
        {
            var s = NewComp(pipe, name, AdoSrc); UseConn(s, cmDbx);
            s.Inst.SetComponentProperty("SqlCommand", query);
            Refresh(s);
            return s;
        }

        static Comp DataConv(MainPipe pipe, string name, IDTSOutput100 from, params object[] colLen)
        {
            var c = NewComp(pipe, name, DataCnv);
            Connect(pipe, from, c);
            var inp = c.Meta.InputCollection[0]; var vi = inp.GetVirtualInput(); var o = c.Meta.OutputCollection[0];
            for (int i = 0; i < colLen.Length; i += 2)
            {
                string col = (string)colLen[i]; var cmap = (ColumnMap)colLen[i + 1]; int len = DwSpec.VarcharLen(cmap.Type);
                var vc = VC(vi, col);
                var ic = c.Inst.SetUsageType(inp.ID, vi, vc.LineageID, DTSUsageType.UT_READONLY);
                var oc = c.Inst.InsertOutputColumnAt(o.ID, o.OutputColumnCollection.Count, "Copy of " + col, "");
                if (DwSpec.IsUnicode(cmap.Type)) c.Inst.SetOutputColumnDataTypeProperties(o.ID, oc.ID, DT.DT_WSTR, len, 0, 0, 0);   // NVARCHAR: Unicode, sin pagina de codigos
                else c.Inst.SetOutputColumnDataTypeProperties(o.ID, oc.ID, DT.DT_STR, len, 0, 0, 1252);
                c.Inst.SetOutputColumnProperty(o.ID, oc.ID, "SourceInputColumnLineageID", ic.LineageID);
            }
            return c;
        }

        Comp Look(MainPipe pipe, string name, IDTSOutput100 from, string table, string[] joinIn, string[] joinRef, string[] outRef, bool failOnNoMatch)
        {
            var c = NewComp(pipe, name, LookupId); UseConn(c, cmSql);
            c.Inst.SetComponentProperty("SqlCommand", "select * from " + table);
            // 0 = una fila sin coincidencia es un error y el componente falla (nunca se pierden hechos en silencio);
            // 1 = se redirige a la salida "No Match" (solo para las metas de Excel, cuyo conteo se reconcilia contra el archivo).
            c.Inst.SetComponentProperty("NoMatchBehavior", failOnNoMatch ? 0 : 1);
            Connect(pipe, from, c);
            Refresh(c);
            var inp = c.Meta.InputCollection[0]; var vi = inp.GetVirtualInput();
            for (int i = 0; i < joinIn.Length; i++)
            {
                var vc = VC(vi, joinIn[i]);
                var ic = c.Inst.SetUsageType(inp.ID, vi, vc.LineageID, DTSUsageType.UT_READONLY);
                c.Inst.SetInputColumnProperty(inp.ID, ic.ID, "JoinToReferenceColumn", joinRef[i]);
            }
            var o = MatchOut(c);
            foreach (var r in outRef)
            {
                var oc = c.Inst.InsertOutputColumnAt(o.ID, o.OutputColumnCollection.Count, r, "");
                c.Inst.SetOutputColumnProperty(o.ID, oc.ID, "CopyFromReferenceColumn", r);
            }
            return c;
        }

        static Comp Derive(MainPipe pipe, string name, IDTSOutput100 from, string newCol, string expr, string refCol)
        {
            var c = NewComp(pipe, name, DerivedId);
            Connect(pipe, from, c);
            var inp = c.Meta.InputCollection[0]; var vi = inp.GetVirtualInput(); var vc = VC(vi, refCol);
            c.Inst.SetUsageType(inp.ID, vi, vc.LineageID, DTSUsageType.UT_READONLY);
            var o = c.Meta.OutputCollection[0];
            var oc = c.Inst.InsertOutputColumnAt(o.ID, 0, newCol, "");
            c.Inst.SetOutputColumnDataTypeProperties(o.ID, oc.ID, DT.DT_I4, 0, 0, 0, 0);
            // en el modelo de objetos, las columnas se referencian como #<lineageID> (sin llaves)
            string tokenExpr = Regex.Replace(expr, @"\b" + Regex.Escape(refCol) + @"\b", "#" + vc.LineageID);
            c.Inst.SetOutputColumnProperty(o.ID, oc.ID, "FriendlyExpression", expr);
            c.Inst.SetOutputColumnProperty(o.ID, oc.ID, "Expression", tokenExpr);
            return c;
        }

        Comp OleDest(MainPipe pipe, string name, IDTSOutput100 from, string table, Dictionary<string, string> map, string fastOpts)
        {
            var d = NewComp(pipe, name, OleDst); UseConn(d, cmSql);
            d.Inst.SetComponentProperty("AccessMode", 3);
            d.Inst.SetComponentProperty("OpenRowset", table);
            if (fastOpts != null) d.Inst.SetComponentProperty("FastLoadOptions", fastOpts);
            Connect(pipe, from, d);
            Refresh(d);
            var inp = d.Meta.InputCollection[0]; var vi = inp.GetVirtualInput();
            foreach (var kv in map)
            {
                var vc = VC(vi, kv.Key);
                var ic = d.Inst.SetUsageType(inp.ID, vi, vc.LineageID, DTSUsageType.UT_READONLY);
                IDTSExternalMetadataColumn100 ec = null;
                foreach (IDTSExternalMetadataColumn100 x in inp.ExternalMetadataColumnCollection) if (x.Name == kv.Value) ec = x;
                if (ec == null) throw new Exception("La columna destino '" + kv.Value + "' no existe en " + table);
                d.Inst.MapInputColumn(inp.ID, ic.ID, ec.ID);
            }
            return d;
        }
    }
}
