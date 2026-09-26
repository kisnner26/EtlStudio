using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace EtlStudio.Core
{
    /// <summary>Un archivo que el usuario suelta en la app (enunciado, presentacion, hoja de calculo, captura...).</summary>
    public class Attachment
    {
        public string Path, Name, Kind /* text | pdf | image */, Text, MediaType;
        public byte[] Data;
        public int Redactions;
        public string Summary { get { return Name + "  ·  " + Kind + (Redactions > 0 ? "  ·  " + Redactions + " secreto(s) ocultado(s)" : ""); } }
    }

    public static class DocReader
    {
        public static readonly string[] Supported = { ".pptx", ".docx", ".xlsx", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".txt", ".md", ".sql", ".json", ".csv" };

        // Credenciales conocidas: se ocultan SIEMPRE antes de enviar texto a la IA.
        static readonly Regex[] Secrets =
        {
            new Regex(@"dapi[0-9a-fA-F]{16,}"),                 // token de Databricks
            new Regex(@"sk-ant-[A-Za-z0-9_\-]{10,}"),           // API key de Anthropic
            new Regex(@"(?i)(password|contrase\w+|pwd|token)\s*[:=]\s*\S{8,}"),
            new Regex(@"(?i)bearer\s+[A-Za-z0-9._\-]{20,}")
        };

        public static string Redact(string text, out int count)
        {
            int n = 0;
            foreach (var r in Secrets) text = r.Replace(text, m => { n++; return "[SECRETO-OCULTO]"; });
            count = n; return text;
        }

        public static Attachment Load(string path)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var a = new Attachment { Path = path, Name = System.IO.Path.GetFileName(path) };
            switch (ext)
            {
                case ".pptx": a.Kind = "text"; a.Text = ReadPptx(path); break;
                case ".docx": a.Kind = "text"; a.Text = ReadDocx(path); break;
                case ".xlsx": a.Kind = "text"; a.Text = DescribeXlsx(path); break;
                case ".pdf": a.Kind = "pdf"; a.Data = File.ReadAllBytes(path); a.MediaType = "application/pdf"; break;
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".webp":
                    a.Kind = "image"; a.Data = File.ReadAllBytes(path);
                    a.MediaType = ext == ".png" ? "image/png" : ext == ".gif" ? "image/gif" : ext == ".webp" ? "image/webp" : "image/jpeg"; break;
                case ".txt": case ".md": case ".sql": case ".json": case ".csv":
                    a.Kind = "text"; a.Text = File.ReadAllText(path); if (a.Text.Length > 200000) a.Text = a.Text.Substring(0, 200000); break;
                default: throw new NotSupportedException("Formato no soportado: " + ext);
            }
            if (a.Kind == "text") { int c; a.Text = Redact(a.Text, out c); a.Redactions = c; }
            return a;
        }

        // ---- PPTX: texto de cada diapositiva, en orden ----
        static string ReadPptx(string path)
        {
            var sb = new StringBuilder();
            using (var z = ZipFile.OpenRead(path))
            {
                var slides = z.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
                    .OrderBy(e => int.Parse(Regex.Match(e.Name, @"\d+").Value)).ToList();
                int i = 0;
                foreach (var s in slides)
                {
                    sb.AppendLine("--- Diapositiva " + (++i) + " ---");
                    sb.AppendLine(TextOf(s, "a:p", "a:t"));
                }
                foreach (var n in z.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/notesSlides/notesSlide\d+\.xml$")))
                { var t = TextOf(n, "a:p", "a:t").Trim(); if (t.Length > 0) sb.AppendLine("[Notas] " + t); }
            }
            return sb.ToString();
        }

        // ---- DOCX: parrafos y tablas ----
        static string ReadDocx(string path)
        {
            using (var z = ZipFile.OpenRead(path))
            {
                var e = z.GetEntry("word/document.xml");
                return e == null ? "" : TextOf(e, "w:p", "w:t");
            }
        }

        static string TextOf(ZipArchiveEntry entry, string paraTag, string textTag)
        {
            var doc = new XmlDocument();
            using (var s = entry.Open()) doc.Load(s);
            var sb = new StringBuilder();
            var nsm = new XmlNamespaceManager(doc.NameTable);
            nsm.AddNamespace("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
            nsm.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
            foreach (XmlNode p in doc.SelectNodes("//" + paraTag, nsm))
            {
                var line = string.Concat(p.SelectNodes(".//" + textTag, nsm).Cast<XmlNode>().Select(t => t.InnerText));
                if (line.Trim().Length > 0) sb.AppendLine(line);
            }
            return sb.ToString();
        }

        // ---- XLSX: hojas y primeras filas (via ACE, como lo hace SSIS) ----
        static string DescribeXlsx(string path)
        {
            var sb = new StringBuilder("ARCHIVO EXCEL: " + path + "\n");
            try
            {
                using (var c = new OleDbConnection("Provider=Microsoft.ACE.OLEDB.16.0;Data Source=" + path + ";Extended Properties='Excel 12.0 Xml;HDR=YES'"))
                {
                    c.Open();
                    var sheets = c.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null);
                    foreach (System.Data.DataRow r in sheets.Rows)
                    {
                        string sheet = Convert.ToString(r["TABLE_NAME"]);
                        if (!sheet.EndsWith("$") && !sheet.EndsWith("$'")) continue;
                        var dt = new System.Data.DataTable();
                        new OleDbDataAdapter("SELECT * FROM [" + sheet + "]", c).Fill(dt);
                        sb.AppendLine("Hoja: " + sheet + "  (" + dt.Rows.Count + " filas)");
                        sb.AppendLine("Columnas: " + string.Join(", ", dt.Columns.Cast<System.Data.DataColumn>().Select(x => x.ColumnName + ":" + x.DataType.Name)));
                        foreach (System.Data.DataRow row in dt.Rows.Cast<System.Data.DataRow>().Take(6)) sb.AppendLine("  " + string.Join(" | ", row.ItemArray));
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("(No se pudo leer el Excel: " + ex.Message + ")"); }
            return sb.ToString();
        }
    }
}
