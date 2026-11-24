using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace App.Shared.Services;

/// <summary>
/// Client-side markdown → PDF renderer (PDFsharp core build, no native assets).
/// Works identically on WASM, MAUI Windows and MAUI Linux; fonts are embedded
/// resources because WASM has no system fonts. Used by the native generate_pdf tool.
/// </summary>
public static class MarkdownPdfService
{
    public sealed record PdfRenderResult(byte[] Bytes, int PageCount);

    private const double Margin = 56;          // ~2 cm
    private const double BodySize = 11;
    private const double CodeSize = 9;
    private const int MaxPages = 80;

    private static int _resolverInstalled;

    public static PdfRenderResult Render(string? title, string markdown, string? pageSize = null)
    {
        EnsureFontResolver();

        var letter = string.Equals(pageSize?.Trim(), "letter", StringComparison.OrdinalIgnoreCase);
        var pageWidth = letter ? 612.0 : 595.28;   // US Letter / A4 in points
        var pageHeight = letter ? 792.0 : 841.89;

        var document = new PdfDocument();
        document.Info.Title = string.IsNullOrWhiteSpace(title) ? "Document" : title.Trim();
        document.Info.Author = "Wizionic";
        document.Info.Creator = "Wizionic";
        document.Info.CreationDate = DateTime.Now;

        var renderer = new Renderer(document, pageWidth, pageHeight);
        renderer.NewPage();

        var doc = Markdown.Parse(markdown ?? "");

        // Avoid a duplicated title when the body already starts with the same H1.
        var skipTitle = false;
        if (!string.IsNullOrWhiteSpace(title) && doc.FirstOrDefault() is HeadingBlock { Level: 1 } first)
        {
            var firstText = string.Concat(first.Inline?.OfType<LiteralInline>().Select(l => l.Content.ToString()) ?? []);
            skipTitle = string.Equals(firstText.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(title) && !skipTitle)
            renderer.Heading(title.Trim(), level: 1);

        foreach (var block in doc)
            renderer.Block(block, renderer.Left, renderer.Right);

        renderer.Finish();

        // PageCount must be read before Save — PDFsharp locks the document after saving.
        var pageCount = document.PageCount;
        using var ms = new MemoryStream();
        document.Save(ms, closeStream: false);
        return new PdfRenderResult(ms.ToArray(), pageCount);
    }

    private static void EnsureFontResolver()
    {
        if (Interlocked.CompareExchange(ref _resolverInstalled, 1, 0) == 0)
            GlobalFontSettings.FontResolver = new EmbeddedPdfFontResolver();
    }

    /// <summary>Serves the embedded Lato (body) / Cousine (code) TTFs to PDFsharp.</summary>
    private sealed class EmbeddedPdfFontResolver : IFontResolver
    {
        public string DefaultFontName => "Lato";

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var mono = familyName.ToLowerInvariant() is "cousine" or "mono" or "monospace" or "courier" or "courier new";
            var style = (isBold, isItalic) switch
            {
                (true, true) => "BoldItalic",
                (true, false) => "Bold",
                (false, true) => "Italic",
                _ => "Regular"
            };
            var face = (mono ? "Cousine-" : "Lato-") + style;
            return new FontResolverInfo(face);
        }

        public byte[]? GetFont(string faceName)
        {
            var prefix = faceName.StartsWith("Cousine-", StringComparison.OrdinalIgnoreCase)
                ? "pdfont.cousine-"
                : "pdfont.lato-";
            var name = prefix + faceName + ".ttf";
            using var stream = typeof(MarkdownPdfService).Assembly.GetManifestResourceStream(name);
            if (stream == null)
                return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
    }

    /// <summary>One styled text fragment produced from markdown inlines.</summary>
    private sealed record Run(string Text, bool Bold, bool Italic, bool Mono);

    private sealed class Renderer
    {
        private readonly PdfDocument _document;
        private readonly double _pageWidth;
        private readonly double _pageHeight;
        private readonly Dictionary<(string Family, double Size, XFontStyleEx Style), XFont> _fonts = new();
        private readonly XBrush _bodyBrush = new XSolidBrush(XColor.FromArgb(32, 33, 36));
        private readonly XBrush _mutedBrush = new XSolidBrush(XColor.FromArgb(110, 114, 120));
        private readonly XBrush _codeBg = new XSolidBrush(XColor.FromArgb(244, 245, 247));
        private readonly XPen _rulePen = new(XColor.FromArgb(210, 213, 218), 0.8);
        private readonly XPen _quotePen = new(XColor.FromArgb(190, 194, 200), 2.5);

        private PdfPage _page = null!;
        private XGraphics _gfx = null!;
        private double _y;
        private Action? _pageBreakFlush;

        public double Left => Margin;
        public double Right => _pageWidth - Margin;
        private double Top => Margin;
        private double Bottom => _pageHeight - Margin - 14; // room for footer

        public Renderer(PdfDocument document, double pageWidth, double pageHeight)
        {
            _document = document;
            _pageWidth = pageWidth;
            _pageHeight = pageHeight;
        }

        public void NewPage()
        {
            _pageBreakFlush?.Invoke();
            if (_document.PageCount >= MaxPages)
                throw new InvalidOperationException($"Document exceeds the {MaxPages}-page limit.");

            _gfx?.Dispose();
            _page = _document.AddPage();
            _page.Width = XUnit.FromPoint(_pageWidth);
            _page.Height = XUnit.FromPoint(_pageHeight);
            _gfx = XGraphics.FromPdfPage(_page);
            _y = Top;
        }

        public void Finish()
        {
            _pageBreakFlush?.Invoke();
            _pageBreakFlush = null;

            // PDFsharp allows only one live XGraphics per page — close the content
            // graphics before drawing footers (including on the last page).
            _gfx.Dispose();
            _gfx = null!;

            var footerFont = Font("Lato", 8, XFontStyleEx.Regular);
            for (var i = 0; i < _document.PageCount; i++)
            {
                var page = _document.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page);
                var text = $"Page {i + 1} of {_document.PageCount}";
                gfx.DrawString(text, footerFont, _mutedBrush,
                    new XRect(0, _pageHeight - 34, _pageWidth, 20), XStringFormats.Center);
            }
        }

        private XFont Font(string family, double size, XFontStyleEx style)
        {
            var key = (family, size, style);
            if (!_fonts.TryGetValue(key, out var font))
            {
                font = new XFont(family, size, style);
                _fonts[key] = font;
            }
            return font;
        }

        private void EnsureSpace(double height)
        {
            if (_y + height > Bottom)
                NewPage();
        }

        public void Block(Block block, double left, double right)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    HeadingInlines(heading.Inline, heading.Level, left, right);
                    break;

                case ParagraphBlock paragraph:
                    DrawInlines(paragraph.Inline, BodySize, left, right, _bodyBrush);
                    _y += 6;
                    break;

                case ListBlock list:
                    List(list, left, right);
                    break;

                case FencedCodeBlock fenced:
                    CodeBlockText(fenced.Lines.ToString(), left, right);
                    break;

                case CodeBlock code:
                    CodeBlockText(code.Lines.ToString(), left, right);
                    break;

                case QuoteBlock quote:
                    Quote(quote, left, right);
                    break;

                case ThematicBreakBlock:
                    EnsureSpace(16);
                    _y += 8;
                    _gfx.DrawLine(_rulePen, left, _y, right, _y);
                    _y += 10;
                    break;

                case HtmlBlock html:
                    var text = StripHtml(html.Lines.ToString());
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        DrawPlainText(text, BodySize, left, right, _bodyBrush);
                        _y += 6;
                    }
                    break;

                case ContainerBlock container:
                    foreach (var child in container)
                        Block(child, left, right);
                    break;
            }
        }

        public void Heading(string text, int level)
        {
            var size = level switch { 1 => 22, 2 => 17, 3 => 14, 4 => 12.5, _ => 11.5 };
            EnsureSpace(size * 1.6);
            _y += level == 1 ? 0 : 8;
            DrawPlainText(text, size, Left, Right, _bodyBrush, bold: true);
            _y += 5;
        }

        private void HeadingInlines(Inline? inline, int level, double left, double right)
        {
            var size = level switch { 1 => 22, 2 => 17, 3 => 14, 4 => 12.5, _ => 11.5 };
            EnsureSpace(size * 1.6);
            _y += level == 1 ? 0 : 8;
            DrawInlines(inline, size, left, right, _bodyBrush, baseBold: true);
            _y += 5;
        }

        private void List(ListBlock list, double left, double right)
        {
            var number = 1;
            foreach (var child in list.OfType<ListItemBlock>())
            {
                var marker = list.IsOrdered ? $"{number}." : "•";
                number++;

                var markerFont = Font("Lato", BodySize, XFontStyleEx.Regular);
                var lineHeight = BodySize * 1.45;
                EnsureSpace(lineHeight);
                _gfx.DrawString(marker, markerFont, _bodyBrush,
                    new XRect(left, _y, 20, lineHeight), XStringFormats.TopLeft);

                var contentLeft = left + 20;
                foreach (var itemBlock in child)
                {
                    if (itemBlock is ListBlock nested)
                        List(nested, contentLeft, right);
                    else if (itemBlock is ParagraphBlock p)
                    {
                        DrawInlines(p.Inline, BodySize, contentLeft, right, _bodyBrush);
                        _y += 2;
                    }
                    else
                        Block(itemBlock, contentLeft, right);
                }
                _y += 3;
            }
            _y += 3;
        }

        private void Quote(QuoteBlock quote, double left, double right)
        {
            var lineLeft = left + 5;
            var contentLeft = left + 16;
            var segmentStart = _y;
            var savedFlush = _pageBreakFlush;
            _pageBreakFlush = () =>
            {
                if (_y > segmentStart)
                    _gfx.DrawLine(_quotePen, lineLeft, segmentStart, lineLeft, _y - 2);
                segmentStart = Top; // NewPage resets _y to Top for the next segment
                savedFlush?.Invoke();
            };

            foreach (var child in quote)
            {
                if (child is ParagraphBlock p)
                {
                    DrawInlines(p.Inline, BodySize, contentLeft, right, _mutedBrush);
                    _y += 4;
                }
                else
                    Block(child, contentLeft, right);
            }

            _pageBreakFlush?.Invoke();
            _pageBreakFlush = savedFlush;
            _y += 4;
        }

        private void CodeBlockText(string text, double left, double right)
        {
            var font = Font("Cousine", CodeSize, XFontStyleEx.Regular);
            var lineHeight = CodeSize * 1.45;
            var charWidth = _gfx.MeasureString("M", font).Width;
            var maxChars = Math.Max(8, (int)((right - left - 14) / charWidth));

            // Expand to wrapped pieces first so each page segment's background
            // can be drawn before its text (PDFsharp paints in draw order).
            var pieces = new List<string>();
            foreach (var rawLine in text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            {
                var line = rawLine.Replace("\t", "    ");
                if (line.Length <= maxChars)
                    pieces.Add(line);
                else
                    pieces.AddRange(Chunk(line, maxChars));
            }
            if (pieces.Count == 0)
                pieces.Add("");

            _y += 4;
            var index = 0;
            while (index < pieces.Count)
            {
                EnsureSpace(lineHeight);
                var fit = Math.Max(1, (int)((Bottom - _y) / lineHeight));
                var count = Math.Min(fit, pieces.Count - index);
                _gfx.DrawRectangle(_codeBg, left, _y - 3, right - left, count * lineHeight + 6);
                for (var i = 0; i < count; i++)
                {
                    _gfx.DrawString(pieces[index + i], font, _bodyBrush,
                        new XRect(left + 7, _y, right - left - 14, lineHeight), XStringFormats.TopLeft);
                    _y += lineHeight;
                }
                index += count;
                if (index < pieces.Count)
                    NewPage();
            }
            _y += 8;
        }

        private static string[] Chunk(string text, int size)
        {
            var parts = new string[(text.Length + size - 1) / size];
            for (var i = 0; i < parts.Length; i++)
                parts[i] = text.Substring(i * size, Math.Min(size, text.Length - i * size));
            return parts;
        }

        /// <summary>Lay out flattened inline runs word by word with wrapping and page breaks.</summary>
        private void DrawInlines(
            Inline? inline, double size, double left, double right, XBrush brush,
            bool baseBold = false, bool baseItalic = false)
        {
            var runs = new List<Run>();
            CollectRuns(inline, runs, baseBold, baseItalic, mono: false);
            DrawRuns(runs, size, left, right, brush);
        }

        private void DrawPlainText(
            string text, double size, double left, double right, XBrush brush, bool bold = false)
        {
            DrawRuns([new Run(text, bold, false, false)], size, left, right, brush);
        }

        private void DrawRuns(List<Run> runs, double size, double left, double right, XBrush brush)
        {
            var lineHeight = size * 1.45;
            EnsureSpace(lineHeight);
            var lineTop = _y;
            var x = left;
            var pendingSpace = false;

            void NewLine()
            {
                lineTop += lineHeight;
                if (lineTop + lineHeight > Bottom)
                {
                    NewPage();
                    lineTop = _y;
                }
                x = left;
                pendingSpace = false;
            }

            foreach (var run in runs)
            {
                if (run.Text.Length == 0)
                    continue;

                var style = (run.Bold, run.Italic) switch
                {
                    (true, true) => XFontStyleEx.BoldItalic,
                    (true, false) => XFontStyleEx.Bold,
                    (false, true) => XFontStyleEx.Italic,
                    _ => XFontStyleEx.Regular
                };
                var font = Font(run.Mono ? "Cousine" : "Lato", run.Mono ? size * 0.92 : size, style);
                var runBrush = run.Mono ? _bodyBrush : brush;
                var spaceWidth = _gfx.MeasureString(" ", font).Width;

                foreach (var part in Regex.Split(run.Text, @"(\s+)"))
                {
                    if (part.Length == 0)
                        continue;
                    if (part.Contains('\n'))
                    {
                        NewLine();
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(part))
                    {
                        pendingSpace = true;
                        continue;
                    }

                    var width = _gfx.MeasureString(part, font).Width;
                    if (pendingSpace && x > left)
                    {
                        x += spaceWidth;
                        pendingSpace = false;
                    }

                    if (width > right - left)
                    {
                        // Overlong token (URL, CJK run, …): break by character.
                        foreach (var ch in part)
                        {
                            var cw = _gfx.MeasureString(ch.ToString(), font).Width;
                            if (x + cw > right && x > left)
                                NewLine();
                            _gfx.DrawString(ch.ToString(), font, runBrush,
                                new XRect(x, lineTop, cw + 1, lineHeight), XStringFormats.TopLeft);
                            x += cw;
                        }
                        continue;
                    }

                    if (x + width > right && x > left)
                        NewLine();

                    _gfx.DrawString(part, font, runBrush,
                        new XRect(x, lineTop, width + 2, lineHeight), XStringFormats.TopLeft);
                    x += width;
                }
            }

            _y = lineTop + lineHeight;
        }

        private static void CollectRuns(Inline? inline, List<Run> runs, bool bold, bool italic, bool mono)
        {
            for (var i = inline; i != null; i = i.NextSibling)
            {
                switch (i)
                {
                    case LiteralInline literal:
                        runs.Add(new Run(literal.Content.ToString(), bold, italic, mono));
                        break;

                    case EmphasisInline emphasis:
                        CollectRuns(emphasis.FirstChild, runs,
                            bold || emphasis.DelimiterCount >= 2,
                            italic || emphasis.DelimiterCount is 1 or 3,
                            mono);
                        break;

                    case CodeInline code:
                        runs.Add(new Run(code.Content, false, false, true));
                        break;

                    case LinkInline link when link.IsImage:
                        runs.Add(new Run("[image]", false, true, false));
                        break;

                    case LinkInline link:
                        var start = runs.Count;
                        CollectRuns(link.FirstChild, runs, bold, italic, mono);
                        var text = string.Concat(runs.Skip(start).Select(r => r.Text));
                        if (!string.IsNullOrWhiteSpace(link.Url) &&
                            !string.Equals(text.Trim(), link.Url.Trim(), StringComparison.OrdinalIgnoreCase))
                            runs.Add(new Run($" ({link.Url})", false, false, false));
                        break;

                    case LineBreakInline lineBreak:
                        runs.Add(new Run(lineBreak.IsHard ? "\n" : " ", bold, italic, mono));
                        break;

                    case HtmlInline html:
                        var stripped = StripHtml(html.Tag);
                        if (!string.IsNullOrWhiteSpace(stripped))
                            runs.Add(new Run(stripped, bold, italic, mono));
                        break;

                    case HtmlEntityInline entity:
                        runs.Add(new Run(entity.Transcoded.ToString(), bold, italic, mono));
                        break;

                    case ContainerInline container:
                        CollectRuns(container.FirstChild, runs, bold, italic, mono);
                        break;
                }
            }
        }

        private static string StripHtml(string text) =>
            Regex.Replace(text, "<[^>]+>", " ").Trim();
    }
}