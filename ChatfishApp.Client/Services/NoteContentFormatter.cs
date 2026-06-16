using Markdig;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Renders note response content for display or Quill editor initialization.
/// Markdown (default) is converted via Markdig; HTML is passed through after Quill saves.
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
}