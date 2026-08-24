using System.Text.RegularExpressions;
using Markdig;

namespace App.Shared.Services;

/// <summary>
/// Renders note response content for display or Quill editor initialization.
/// </summary>
public static class NoteContentFormatter
{
    public const string FormatHtml = "html";
    public const string FormatMarkdown = "markdown";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .Build();

    public static bool IsHtml(string? contentFormat) =>
        string.Equals(contentFormat, FormatHtml, StringComparison.OrdinalIgnoreCase);

    public static string ToDisplayHtml(string content, string? contentFormat)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        return ToQuillHtml(content);
    }

    public static string ToEditorHtml(string content, string? contentFormat)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "<p><br></p>";

        return ToQuillHtml(content);
    }

    /// <summary>
    /// Convert model markdown/HTML into Quill-friendly HTML so view mode matches the editor
    /// (Quill bullets use <c>ol &gt; li[data-list]</c>; extra empty paragraphs are collapsed).
    /// </summary>
    public static string ToQuillHtml(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "<p><br></p>";

        var html = LooksLikeHtml(content)
            ? content.Trim()
            : Markdown.ToHtml(content.Trim(), Pipeline);

        html = Regex.Replace(
            html,
            @"<li([^>]*)>\s*<p>(.*?)</p>\s*</li>",
            "<li$1>$2</li>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        html = Regex.Replace(
            html,
            @"<ul\b[^>]*>(.*?)</ul>",
            m => "<ol>" + AddListAttr(m.Groups[1].Value, "bullet") + "</ol>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        html = Regex.Replace(
            html,
            @"<ol\b[^>]*>(.*?)</ol>",
            m =>
            {
                var inner = m.Groups[1].Value;
                if (inner.Contains("data-list=", StringComparison.OrdinalIgnoreCase))
                    return "<ol>" + inner + "</ol>";
                return "<ol>" + AddListAttr(inner, "ordered") + "</ol>";
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Drop spacer paragraphs and pretty-print newlines. Quill view uses a real .ql-editor
        // (white-space: pre-wrap) so leftover source whitespace shows as blank lines.
        html = Regex.Replace(
            html,
            @"<p>(?:\s|&nbsp;|<br\s*/?>)*</p>",
            "",
            RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @">\s+<", "><");
        html = Regex.Replace(html, @"(?:<br\s*/?>\s*){2,}", "<br>", RegexOptions.IgnoreCase);

        return string.IsNullOrWhiteSpace(html) ? "<p><br></p>" : html.Trim();
    }

    private static string AddListAttr(string inner, string listKind) =>
        Regex.Replace(
            inner,
            @"<li\b([^>]*)>",
            m =>
            {
                var attrs = m.Groups[1].Value;
                if (attrs.Contains("data-list", StringComparison.OrdinalIgnoreCase))
                    return "<li" + attrs + ">";
                return $"<li data-list=\"{listKind}\"{attrs}>";
            },
            RegexOptions.IgnoreCase);

    private static bool LooksLikeHtml(string s) =>
        s.Contains('<') && s.Contains('>') &&
        Regex.IsMatch(
            s,
            @"</?(p|div|h[1-6]|ul|ol|li|br|table|span|strong|em|blockquote)\b",
            RegexOptions.IgnoreCase);
}