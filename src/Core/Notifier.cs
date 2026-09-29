using System;
using System.Net;
using System.Text;

namespace EtlStudio.Core
{
    /// <summary>Avisa el resultado de una corrida a un webhook (Teams, Slack, Power Automate...) con un JSON simple.</summary>
    public static class Notifier
    {
        public static string Json(string dw, bool ok, string message, string reportPath)
        {
            Func<string, string> q = t => "\"" + (t ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ") + "\"";
            return "{\"text\":" + q((ok ? "OK - " : "FALLO - ") + dw + ": " + message) + ",\"dw\":" + q(dw) + ",\"ok\":" + (ok ? "true" : "false") +
                   ",\"message\":" + q(message) + ",\"report\":" + q(reportPath) + ",\"host\":" + q(Environment.MachineName) + "}";
        }

        public static bool IsValidUrl(string url)
        {
            Uri u; return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) && u.Scheme == Uri.UriSchemeHttps;
        }

        /// <summary>Devuelve null si se envio, o el motivo del error. Solo HTTPS.</summary>
        public static string Send(string url, string dw, bool ok, string message, string reportPath)
        {
            if (!IsValidUrl(url)) return "La URL de notificacion debe ser HTTPS.";
            try
            {
                using (var w = new WebClient { Encoding = Encoding.UTF8 })
                {
                    w.Headers[HttpRequestHeader.ContentType] = "application/json";
                    w.UploadString(url, "POST", Json(dw, ok, message, reportPath));
                }
                return null;
            }
            catch (Exception ex) { AppLog.Error("No se pudo enviar la notificacion", ex); return ex.Message; }
        }
    }
}
