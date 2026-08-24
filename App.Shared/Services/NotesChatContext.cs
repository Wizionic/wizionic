using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Storage;

namespace App.Shared.Services;

/// <summary>
/// Serializes a Notes → Chat handoff as a conversation message the UI can render as a card
/// and the completion path can expand into model context.
/// </summary>
public static class NotesChatContext
{
    public const string ContentFormat = "note-context";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool Is(ChatMessage message) =>
        string.Equals(message.ContentFormat, ContentFormat, StringComparison.OrdinalIgnoreCase);

    public static ChatMessage CreateMessage(NotesChatHandoffPayload payload) =>
        new(
            Role: "user",
            Content: JsonSerializer.Serialize(payload, JsonOptions),
            Timestamp: DateTime.UtcNow,
            ItemId: Guid.NewGuid().ToString("N"),
            ContentFormat: ContentFormat);

    public static bool TryParse(ChatMessage message, out NotesChatHandoffPayload payload)
    {
        payload = null!;
        if (!Is(message) || string.IsNullOrWhiteSpace(message.Content))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<NotesChatHandoffPayload>(message.Content, JsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.NotebookId))
                return false;
            payload = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToLlmText(NotesChatHandoffPayload payload)
    {
        var plain = HtmlToPlain(payload.Html);
        if (plain.Length > 24_000)
            plain = plain[..24_000] + "\n…[truncated]";

        var html = payload.Html ?? "";
        if (html.Length > 32_000)
            html = html[..32_000] + "\n…[truncated]";

        var entry = string.IsNullOrWhiteSpace(payload.EntryId) ? "(unknown)" : payload.EntryId;
        return
            "The user opened a note from their notebook to work on it with you. " +
            "The note is already available — they will tell you what to do (summarize, restructure, make a table, etc.).\n\n" +
            $"Notebook: \"{payload.NotebookTitle}\" (notebook_id={payload.NotebookId})\n" +
            $"Entry id: {entry}\n\n" +
            "Note as plain text:\n" +
            plain +
            "\n\nNote as HTML (preserve structure if rewriting):\n" +
            html +
            "\n\nDo not overwrite the notebook unless the user explicitly asks to save/overwrite/update it. " +
            "Summarize and rewrite in the chat reply first. If they confirm it looks good and want it saved, " +
            "call update_note_entry with overwrite_confirmed=true (compact Quill HTML, " +
            "<ol><li data-list=\"bullet\"> for bullets). " +
            "Use add_note_entry only when they ask to add a new entry (leave the original alone).";
    }

    public const string SystemReminder =
        "This chat was opened from a notebook so you can READ the note. " +
        "Do not call update_note_entry unless the user explicitly asks to save, overwrite, replace, or update the existing note " +
        "(e.g. \"save that to my note\", \"overwrite it\", \"update the notebook\", \"looks good, save it\"). " +
        "Show summaries and rewrites in the chat reply first. " +
        "If they want a new entry instead of replacing, use add_note_entry only when they ask to add/save a new entry. " +
        "When overwriting, pass overwrite_confirmed=true and send compact Quill HTML " +
        "(use <ol><li data-list=\"bullet\"> for bullets; do not insert empty <p><br></p> between items).";

    public static string PreviewPlain(NotesChatHandoffPayload payload, int maxChars = 180)
    {
        var plain = HtmlToPlain(payload.Html);
        if (string.IsNullOrWhiteSpace(plain))
            return "(empty note)";
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        if (plain.Length <= maxChars)
            return plain;
        return plain[..maxChars].TrimEnd() + "…";
    }

    public static string HtmlToPlain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var text = Regex.Replace(html, "(?i)<br\\s*/?>", "\n");
        text = Regex.Replace(text, "(?i)</(p|div|li|h[1-6]|tr|blockquote)>", "\n");
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "[ \\t]+", " ");
        text = Regex.Replace(text, "\\n{3,}", "\n\n");
        return text.Trim();
    }
}
