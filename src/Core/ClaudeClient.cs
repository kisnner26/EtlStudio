using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace EtlStudio.Core
{
    /// <summary>Cliente minimo de la API de Anthropic (Messages), con texto, PDF e imagenes. Usa tu propia API key.</summary>
    public class ClaudeClient
    {
        readonly string apiKey, model, workspaceId;
        public ClaudeClient(string apiKey, string model, string workspaceId = null) { this.apiKey = apiKey; this.model = model; this.workspaceId = workspaceId; }

        public class Turn
        {
            public string Role, Text;
            public List<Attachment> Attachments = new List<Attachment>();
        }

        public string Send(string system, IList<Turn> turns, int maxTokens = 8192)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Falta la API key de Anthropic (Ajustes).");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var msgs = new List<Dictionary<string, object>>();
            foreach (var t in turns)
            {
                var content = new List<object>();
                foreach (var a in t.Attachments)
                {
                    if (a.Kind == "pdf")
                        content.Add(new Dictionary<string, object> { { "type", "document" }, { "source", new Dictionary<string, object> { { "type", "base64" }, { "media_type", a.MediaType }, { "data", Convert.ToBase64String(a.Data) } } } });
                    else if (a.Kind == "image")
                        content.Add(new Dictionary<string, object> { { "type", "image" }, { "source", new Dictionary<string, object> { { "type", "base64" }, { "media_type", a.MediaType }, { "data", Convert.ToBase64String(a.Data) } } } });
                    else
                        content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", "=== Archivo: " + a.Name + " ===\n" + a.Text } });
                }
                content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", t.Text ?? "" } });
                msgs.Add(new Dictionary<string, object> { { "role", t.Role }, { "content", content } });
            }
            var body = new Dictionary<string, object> { { "model", model }, { "max_tokens", maxTokens }, { "system", system }, { "messages", msgs } };
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            byte[] data = Encoding.UTF8.GetBytes(ser.Serialize(body));

            var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/v1/messages");
            req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 300000;
            req.Headers["x-api-key"] = apiKey; req.Headers["anthropic-version"] = "2023-06-01";
            if (!string.IsNullOrWhiteSpace(workspaceId)) req.Headers["anthropic-workspace-id"] = workspaceId.Trim();
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return ExtractText(ser.DeserializeObject(rd.ReadToEnd()));
            }
            catch (WebException ex) when (ex.Response != null)
            {
                string detail; using (var rd = new StreamReader(ex.Response.GetResponseStream())) detail = rd.ReadToEnd();
                throw new Exception("Error de la API de Anthropic: " + detail);
            }
        }

        static string ExtractText(object json)
        {
            var root = json as Dictionary<string, object>;
            var sb = new StringBuilder();
            var arr = root != null && root.ContainsKey("content") ? root["content"] as object[] : null;
            if (arr != null)
                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    if (d != null && Convert.ToString(d["type"]) == "text") sb.Append(Convert.ToString(d["text"]));
                }
            return sb.ToString();
        }

        /// <summary>Saca el primer objeto JSON de una respuesta (tolera ```json ... ```).</summary>
        public static string ExtractJson(string text)
        {
            int a = text.IndexOf('{'), b = text.LastIndexOf('}');
            if (a < 0 || b <= a) throw new Exception("La respuesta no contiene JSON.");
            return text.Substring(a, b - a + 1);
        }

        const string SpecShape =
@"{
 ""DwName"": ""DW_Nombre"", ""SqlServer"": ""<lo rellena la app>"", ""SourceDsn"": ""<lo rellena la app>"", ""SourceQuery"": ""<SELECT de origen completo>"",
 ""Time"": { ""Name"": ""Dim_Tiempo"", ""From"": ""AAAA-MM-DD"", ""To"": ""AAAA-MM-DD"" },
 ""Dims"": [ { ""Name"": ""Dim_X"", ""Key"": ""SK_X"", ""Columns"": [ { ""Src"": ""col_origen"", ""Dst"": ""Col_Destino"", ""Type"": ""VARCHAR(100)"" } ] } ],
 ""Fact"": { ""Name"": ""Fact_X"", ""DateColumn"": ""columna_fecha_origen"",
   ""BusinessKey"": { ""Src"": ""id_origen"", ""Dst"": ""ID_Origen"", ""Type"": ""VARCHAR(50)"" },
   ""Measures"": [ { ""Src"": ""col_num"", ""Dst"": ""Col_Num"", ""Type"": ""DECIMAL(18,2)"" } ],
   ""Computed"": [ { ""Name"": ""Ingreso_Total"", ""Expr"": ""(Precio + Extras)"" } ] },
 ""Goals"": { ""Table"": ""Fact_Metas"", ""ExcelPath"": ""<ruta del xlsx adjunto>"", ""Sheet"": ""Hoja$"", ""DimName"": ""Dim_X"", ""ExcelDimColumn"": ""Sucursal"", ""ExcelDateColumn"": ""Fecha"", ""ExcelMeasureColumn"": ""Monto_Meta"" }
}";

        const string SpecRules =
@"Reglas del diseno: identificadores solo [A-Za-z0-9_]; tipos permitidos: VARCHAR(n), INT, BIGINT, DECIMAL(p,s), DATE, DATETIME, BIT, FLOAT;
las columnas de dimension y la BusinessKey deben ser VARCHAR; Src debe ser una columna que devuelve el SourceQuery; las dimensiones agrupan atributos
descriptivos (texto), las medidas son numericas; Computed usa solo nombres de medidas (Dst), parentesis y + - * /; DateColumn es una columna de fecha del SELECT.
Goals solo si hay un Excel de metas/presupuestos adjunto (usa su hoja y columnas reales); si no, null. Si el enunciado da nombres exactos de tablas o columnas, respetalos.";

        public const string DesignSystemPrompt =
"Eres un arquitecto de Data Warehouses (modelo en estrella, Kimball) que trabaja con SQL Server y SSIS.\n" +
"Recibes el esquema y filas de muestra de un SELECT de origen (Databricks via ODBC). Propon el DW.\n" +
"Responde UNICAMENTE con un objeto JSON valido (sin texto extra) con esta forma:\n" + SpecShape + "\n" + SpecRules;

        public static readonly string PlannerSystemPrompt =
"Eres el motor de planificacion de EtlStudio, una app de Windows que construye Data Warehouses en SQL Server y paquetes SSIS desde un origen Databricks (ODBC).\n" +
"El usuario te da un enunciado o tarea (texto, PPTX, DOCX, PDF, capturas, hojas de Excel). Extrae lo necesario y devuelve UNICAMENTE un objeto JSON valido (sin texto extra):\n" +
"{\n \"Titulo\": \"titulo corto\", \"Resumen\": \"2-4 frases: que se va a construir\",\n" +
" \"Conexion\": { \"Host\": \"hostname de Databricks si aparece, si no vacio\", \"HttpPath\": \"/sql/1.0/warehouses/... si aparece, si no vacio\", \"DsnSugerido\": \"nombre de DSN\" },\n" +
" \"Spec\": " + SpecShape.Replace("\n", "\n ") + ",\n" +
" \"Pasos\": [\"pasos que ejecutara la app, en lenguaje simple\"], \"Advertencias\": [\"dudas, supuestos o datos que faltan\"]\n}\n" +
"Nunca escribas tokens, contrasenas ni API keys (los archivos ya vienen con los secretos ocultos como [SECRETO-OCULTO]). Si algo no esta claro, elige lo mas razonable y anotalo en Advertencias.\n" + SpecRules;

        public const string ChatSystemPrompt =
@"Eres el asistente de EtlStudio, una app de Windows que crea Data Warehouses en SQL Server y paquetes SSIS desde un origen Databricks/ODBC.
Ayuda al usuario a entender el modelo, explicar resultados de verificacion y diagnosticar errores de SSIS/SQL. Responde en espanol, breve y concreto.
Nunca pidas ni repitas tokens o contrasenas.";
    }
}
