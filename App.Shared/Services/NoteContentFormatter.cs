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

        return IsHtml(contentFormat) ? content : Markdown.ToHtml(content, Pipeline);
    }

    public static string ToEditorHtml(string content, string? contentFormat)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "<p><br></p>";

        return IsHtml(contentFormat) ? content : Markdown.ToHtml(content, Pipeline);
    }

    /// <summary>
    /// AI-only: convert model markdown/HTML into Quill-friendly HTML.
    /// Does not run on manual editor saves. Consecutive blank paragraphs collapse to one
    /// so a single blank line between sections is kept.
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

        // Pretty-printed source newlines between tags; keep a single blank paragraph.
        html = Regex.Replace(html, @">\s+<", "><");
        html = Regex.Replace(
            html,
            @"(?:<p>(?:\s|&nbsp;|<br\s*/?>)*</p>){2,}",
            "<p><br></p>",
            RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"(?:<br\s*/?>\s*){3,}", "<br><br>", RegexOptions.IgnoreCase);

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