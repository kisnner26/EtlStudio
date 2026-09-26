using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace EtlStudio.Core
{
    /// <summary>Cliente minimo de la API de Anthropic (Messages). Usa tu propia API key.</summary>
    public class ClaudeClient
    {
        readonly string apiKey, model;
        public ClaudeClient(string apiKey, string model) { this.apiKey = apiKey; this.model = model; }

        public class Turn { public string Role; public string Text; }

        public string Send(string system, IList<Turn> turns, int maxTokens = 4096)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Falta la API key de Anthropic (pestana Conexiones).");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var msgs = new List<Dictionary<string, object>>();
            foreach (var t in turns) msgs.Add(new Dictionary<string, object> { { "role", t.Role }, { "content", t.Text } });
            var body = new Dictionary<string, object> { { "model", model }, { "max_tokens", maxTokens }, { "system", system }, { "messages", msgs } };
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            byte[] data = Encoding.UTF8.GetBytes(ser.Serialize(body));

            var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/v1/messages");
            req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 180000;
            req.Headers["x-api-key"] = apiKey; req.Headers["anthropic-version"] = "2023-06-01";
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

        public const string DesignSystemPrompt =
@"Eres un arquitecto de Data Warehouses (modelo en estrella, Kimball) que trabaja con SQL Server y SSIS.
Recibes el esquema y unas filas de muestra de un SELECT de origen (Databricks via ODBC). Debes proponer el DW.
Responde UNICAMENTE con un objeto JSON valido (sin texto extra) con esta forma exacta:
{
 ""DwName"": ""DW_Nombre"", ""SqlServer"": ""<se rellena luego>"", ""SourceDsn"": ""<se rellena luego>"", ""SourceQuery"": ""<el SELECT recibido, sin cambios>"",
 ""Time"": { ""Name"": ""Dim_Tiempo"", ""From"": ""AAAA-MM-DD"", ""To"": ""AAAA-MM-DD"" },
 ""Dims"": [ { ""Name"": ""Dim_X"", ""Key"": ""SK_X"", ""Columns"": [ { ""Src"": ""col_origen"", ""Dst"": ""Col_Destino"", ""Type"": ""VARCHAR(100)"" } ] } ],
 ""Fact"": { ""Name"": ""Fact_X"", ""DateColumn"": ""columna_fecha_origen"",
   ""BusinessKey"": { ""Src"": ""id_origen"", ""Dst"": ""ID_Origen"", ""Type"": ""VARCHAR(50)"" },
   ""Measures"": [ { ""Src"": ""col_num"", ""Dst"": ""Col_Num"", ""Type"": ""DECIMAL(18,2)"" } ],
   ""Computed"": [ { ""Name"": ""Ingreso_Total"", ""Expr"": ""(Precio + Extras)"" } ] },
 ""Goals"": null
}
Reglas: identificadores solo [A-Za-z0-9_]; tipos permitidos: VARCHAR(n), INT, BIGINT, DECIMAL(p,s), DATE, DATETIME, BIT, FLOAT;
las columnas de dimension y la BusinessKey deben ser VARCHAR; Src debe ser una columna que existe en el SELECT; las dimensiones agrupan atributos descriptivos
(texto) y las medidas son numericas; Computed usa solo nombres de medidas (Dst), parentesis y + - * /; el fecha debe ser una columna de fecha del SELECT.
Deja Goals en null salvo que se te pida lo contrario. Usa nombres en espanol con el estilo Dim_/Fact_/SK_.";

        public const string ChatSystemPrompt =
@"Eres el asistente de EtlStudio, una app de Windows que crea Data Warehouses en SQL Server y paquetes SSIS desde un origen Databricks/ODBC.
Ayuda al usuario a entender el modelo, explicar resultados de verificacion y diagnosticar errores de SSIS/SQL. Responde en espanol, breve y concreto.
Nunca pidas ni repitas tokens o contrasenas.";
    }
}
