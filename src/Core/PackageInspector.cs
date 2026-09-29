using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace EtlStudio.Core
{
    /// <summary>Lo que se lee de un .dtsx: orden de las tareas, proteccion, lookups y la expresion de SK_Tiempo.</summary>
    public class PackageInfo
    {
        public string ProtectionLevel = "";
        public List<string> TaskOrder = new List<string>();
        /// <summary>(flujo, componente) -> valor de NoMatchBehavior (0 = falla, 1 = redirige, 2 = ignora).</summary>
        public Dictionary<string, string> LookupBehavior = new Dictionary<string, string>();
        /// <summary>flujo -> expresion de SK_Tiempo.</summary>
        public Dictionary<string, string> SkTiempoExpr = new Dictionary<string, string>();
    }

    /// <summary>
    /// Prueba de humo de un paquete generado: lo abre como XML (no necesita SSIS ni bases de datos) y comprueba las garantias
    /// que el generador promete. Coincide por nombre local de elemento y atributo para tolerar los prefijos de espacio de nombres (DTS:).
    /// </summary>
    public static class PackageInspector
    {
        static string Attr(XElement e, string local)
        {
            var a = e.Attributes().FirstOrDefault(x => x.Name.LocalName == local);
            return a == null ? null : a.Value;
        }

        static string Last(string path) { var i = (path ?? "").LastIndexOf('\\'); return i < 0 ? path : path.Substring(i + 1); }

        public static PackageInfo Read(string xml)
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root; var info = new PackageInfo { ProtectionLevel = Attr(root, "ProtectionLevel") ?? "" };

            var next = new Dictionary<string, string>(); var hasPred = new HashSet<string>(); var names = new List<string>();
            foreach (var e in root.Descendants().Where(x => x.Name.LocalName == "PrecedenceConstraint"))
            {
                string f = Last(Attr(e, "From")), t = Last(Attr(e, "To"));
                if (f != null && t != null) { next[f] = t; hasPred.Add(t); if (!names.Contains(f)) names.Add(f); if (!names.Contains(t)) names.Add(t); }
            }
            var start = names.FirstOrDefault(n => !hasPred.Contains(n));
            for (var cur = start; cur != null && !info.TaskOrder.Contains(cur); next.TryGetValue(cur, out cur)) info.TaskOrder.Add(cur);

            foreach (var c in root.Descendants().Where(x => x.Name.LocalName == "component"))
            {
                var flow = c.Ancestors().Where(a => a.Name.LocalName == "Executable" && Attr(a, "ObjectName") != null).Select(a => Attr(a, "ObjectName")).FirstOrDefault() ?? "";
                var cname = Attr(c, "name") ?? "";
                foreach (var p in c.Descendants().Where(x => x.Name.LocalName == "property"))
                {
                    var pn = Attr(p, "name");
                    if (pn == "NoMatchBehavior") info.LookupBehavior[flow + "|" + cname] = p.Value.Trim();
                    if ((pn == "FriendlyExpression" || pn == "Expression") && cname == "DC_SK_Tiempo" && p.Value.Contains("YEAR") && !info.SkTiempoExpr.ContainsKey(flow)) info.SkTiempoExpr[flow] = p.Value;
                }
            }
            return info;
        }

        /// <summary>Tareas que debe contener el paquete, en orden, segun el diseno.</summary>
        public static List<string> ExpectedOrder(DwSpec s)
        {
            var l = new List<string> { "SQL_Auditoria_Inicio", "SQL_Preparar_Staging", "DFT_Poblar_Dimensiones", "DFT_Extraer_Origen" };
            if (s.Goals != null) l.Add("DFT_Cargar_Metas_Excel");
            l.Add("SQL_Publicar"); l.Add("SQL_Auditoria_Fin");
            return l;
        }

        /// <summary>Lista de problemas (vacia = el paquete cumple lo prometido).</summary>
        public static List<string> Verify(DwSpec s, PackageInfo p)
        {
            var e = new List<string>();
            if (p.ProtectionLevel != "0") e.Add("ProtectionLevel deberia ser 0 (DontSaveSensitive) y es '" + p.ProtectionLevel + "'");
            var exp = ExpectedOrder(s);
            if (!p.TaskOrder.SequenceEqual(exp)) e.Add("Orden de tareas inesperado. Esperado: " + string.Join(" -> ", exp) + ". Encontrado: " + string.Join(" -> ", p.TaskOrder));
            foreach (var d in s.Dims)
            {
                string key = "DFT_Extraer_Origen|LK_" + d.Name.Replace("Dim_", "");
                string v; if (!p.LookupBehavior.TryGetValue(key, out v)) e.Add("Falta el lookup de " + d.Name + " en DFT_Extraer_Origen");
                else if (v != "0") e.Add("El lookup de " + d.Name + " tiene NoMatchBehavior=" + v + ": una fila sin dimension no debe descartarse, debe fallar (0)");
            }
            string sk; if (!p.SkTiempoExpr.TryGetValue("DFT_Extraer_Origen", out sk)) e.Add("Falta la columna derivada SK_Tiempo en DFT_Extraer_Origen");
            else if (!(sk.Contains("YEAR(") && sk.Contains("MONTH(") && sk.Contains("DAY("))) e.Add("SK_Tiempo debe calcularse con YEAR/MONTH/DAY sobre la fecha real: " + sk);
            return e;
        }
    }
}
