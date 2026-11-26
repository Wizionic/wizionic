using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace App.Shared.Services;

/// <summary>
/// Lightweight, cross-platform .docx reader (no native deps; works on WASM).
/// Extracts a readable HTML preview and plain text for indexing: headings,
/// paragraphs, bold/italic runs, simple lists and tables. Not a full Word renderer.
/// </summary>
public static class DocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static (string Html, string Text) Read(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("Not a valid .docx file (missing word/document.xml).");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var body = doc.Root?.Element(W + "body");
        if (body is null) return ("", "");

        var html = new StringBuilder();
        var text = new StringBuilder();
        var inList = false;

        foreach (var el in body.Elements())
        {
            if (el.Name == W + "p")
            {
                var (pHtml, pText, isItem) = Paragraph(el);
                if (isItem)
                {
                    if (!inList) { html.Append("<ul>"); inList = true; }
                    html.Append("<li>").Append(pHtml).Append("</li>");
                }
                else
                {
                    if (inList) { html.Append("</ul>"); inList = false; }
                    html.Append(pHtml);
                }
                if (pText.Length > 0) text.AppendLine(pText);
            }
            else if (el.Name == W + "tbl")
            {
                if (inList) { html.Append("</ul>"); inList = false; }
                html.Append("<table>");
                foreach (var row in el.Elements(W + "tr"))
                {
                    html.Append("<tr>");
                    var cells = new List<string>();
                    foreach (var cell in row.Elements(W + "tc"))
                    {
                        var cellText = string.Join(" ", cell.Elements(W + "p").Select(p => Plain(p)));
                        cells.Add(cellText);
                        html.Append("<td>").Append(WebUtility.HtmlEncode(cellText)).Append("</td>");
                    }
                    html.Append("</tr>");
                    text.AppendLine(string.Join("\t", cells));
                }
                html.Append("</table>");
            }
        }
        if (inList) html.Append("</ul>");
        return (html.ToString(), text.ToString().Trim());
    }

    private static (string Html, string Text, bool IsItem) Paragraph(XElement p)
    {
        var props = p.Element(W + "pPr");
        var style = props?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        var isItem = props?.Element(W + "numPr") is not null;

        var inner = new StringBuilder();
        var plain = new StringBuilder();
        foreach (var run in p.Elements(W + "r"))
        {
            var t = string.Concat(run.Elements(W + "t").Select(x => x.Value));
            if (run.Elements(W + "br").Any()) { inner.Append("<br>"); plain.Append(' '); }
            if (t.Length == 0) continue;
            var enc = WebUtility.HtmlEncode(t);
            var rPr = run.Element(W + "rPr");
            if (rPr?.Element(W + "b") is not null) enc = "<strong>" + enc + "</strong>";
            if (rPr?.Element(W + "i") is not null) enc = "<em>" + enc + "</em>";
            inner.Append(enc);
            plain.Append(t);
        }

        var text = plain.ToString();
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(style.AsSpan(7), out var level) && level is >= 1 and <= 6)
            return ($"<h{level}>{inner}</h{level}>", text, false);
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase))
            return ($"<h1>{inner}</h1>", text, false);
        if (isItem) return (inner.ToString(), text, true);
        return ($"<p>{inner}</p>", text, false);
    }

    private static string Plain(XElement p) =>
        string.Concat(p.Elements(W + "r").SelectMany(r => r.Elements(W + "t")).Select(x => x.Value));
}