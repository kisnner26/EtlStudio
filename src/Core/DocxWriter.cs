using System;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace EtlStudio.Core
{
    /// <summary>Generador minimo de .docx (sin dependencias): titulos, parrafos, viñetas, codigo y tablas.</summary>
    public class DocxWriter
    {
        readonly StringBuilder body = new StringBuilder();
        static string E(string s) { return SecurityElement.Escape(s ?? ""); }

        static string Run(string text, bool bold = false, int size = 22, string color = null, string font = null)
        {
            return "<w:r><w:rPr>" + (font != null ? "<w:rFonts w:ascii=\"" + font + "\" w:hAnsi=\"" + font + "\"/>" : "") + (bold ? "<w:b/>" : "") +
                   (color != null ? "<w:color w:val=\"" + color + "\"/>" : "") + "<w:sz w:val=\"" + size + "\"/></w:rPr><w:t xml:space=\"preserve\">" + E(text) + "</w:t></w:r>";
        }

        public DocxWriter Title(string t, string subtitle = null)
        {
            body.Append("<w:p><w:pPr><w:spacing w:after=\"60\"/></w:pPr>" + Run(t, true, 48, "1D1D1F") + "</w:p>");
            if (subtitle != null) body.Append("<w:p><w:pPr><w:spacing w:after=\"240\"/></w:pPr>" + Run(subtitle, false, 22, "6E6E73") + "</w:p>");
            return this;
        }

        public DocxWriter Heading(string t)
        {
            body.Append("<w:p><w:pPr><w:keepNext/><w:spacing w:before=\"280\" w:after=\"100\"/></w:pPr>" + Run(t, true, 30, "0071E3") + "</w:p>");
            return this;
        }

        public DocxWriter Para(string t, bool bold = false)
        {
            body.Append("<w:p><w:pPr><w:spacing w:after=\"100\"/></w:pPr>" + Run(t, bold) + "</w:p>");
            return this;
        }

        public DocxWriter Bullet(string t)
        {
            body.Append("<w:p><w:pPr><w:ind w:left=\"360\" w:hanging=\"240\"/><w:spacing w:after=\"40\"/></w:pPr>" + Run("•  " + t) + "</w:p>");
            return this;
        }

        public DocxWriter Code(string t)
        {
            foreach (var line in (t ?? "").Replace("\r", "").Split('\n'))
                body.Append("<w:p><w:pPr><w:shd w:val=\"clear\" w:fill=\"F5F5F7\"/><w:spacing w:after=\"0\"/></w:pPr>" + Run(line, false, 18, "1D1D1F", "Consolas") + "</w:p>");
            body.Append("<w:p/>");
            return this;
        }

        public DocxWriter Table(DataTable dt, int maxRows = 25)
        {
            if (dt == null || dt.Columns.Count == 0) return this;
            body.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>");
            foreach (var b in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
                body.Append("<w:" + b + " w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"D2D2D7\"/>");
            body.Append("</w:tblBorders><w:tblCellMar><w:left w:w=\"80\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>");
            foreach (DataColumn c in dt.Columns) body.Append("<w:gridCol w:w=\"" + (9000 / dt.Columns.Count) + "\"/>");
            body.Append("</w:tblGrid><w:tr>");
            foreach (DataColumn c in dt.Columns)
                body.Append("<w:tc><w:tcPr><w:shd w:val=\"clear\" w:fill=\"F5F5F7\"/></w:tcPr><w:p>" + Run(c.ColumnName, true, 18) + "</w:p></w:tc>");
            body.Append("</w:tr>");
            foreach (DataRow r in dt.Rows.Cast<DataRow>().Take(maxRows))
            {
                body.Append("<w:tr>");
                foreach (var v in r.ItemArray)
                {
                    string s = v is DateTime ? ((DateTime)v).ToString("yyyy-MM-dd") : Convert.ToString(v);
                    body.Append("<w:tc><w:p>" + Run(s, false, 18) + "</w:p></w:tc>");
                }
                body.Append("</w:tr>");
            }
            body.Append("</w:tbl><w:p/>");
            if (dt.Rows.Count > maxRows) Para("(" + (dt.Rows.Count - maxRows) + " filas mas no se muestran)");
            return this;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) File.Delete(path);
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Add(z, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
                Add(z, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
                Add(z, "word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body +
                    "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>");
            }
        }

        static void Add(ZipArchive z, string name, string content)
        {
            var e = z.CreateEntry(name);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
        }
    }
}
