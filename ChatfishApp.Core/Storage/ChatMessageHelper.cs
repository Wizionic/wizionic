namespace ChatfishApp.Core.Storage;

public static class ChatMessageHelper
{
    private static readonly HashSet<string> GenericNoteTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Untitled", "New note", "(empty)", "(deleted)"
    };

    private static readonly HashSet<string> GenericConvoTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "(empty)", "(deleted)"
    };

    public static bool IsVisible(ChatMessage msg) => !msg.DeletedAt.HasValue;

    public static List<ChatMessage> NormalizeAll(IEnumerable<ChatMessage> messages) =>
        messages.Select(Normalize).ToList();

    public static ChatMessage Normalize(ChatMessage msg)
    {
        var itemId = string.IsNullOrWhiteSpace(msg.ItemId) ? Guid.NewGuid().ToString("N") : msg.ItemId;
        var timestamp = msg.Timestamp ?? msg.ModifiedAt ?? DateTime.UtcNow;
        return msg with { ItemId = itemId, Timestamp = timestamp };
    }

    public static ChatMessage SoftDelete(ChatMessage msg)
    {
        var now = DateTime.UtcNow;
        return msg with { DeletedAt = now, ModifiedAt = now };
    }

    public static ChatMessage TouchModified(ChatMessage msg, string? content = null, string? contentFormat = null)
    {
        var now = DateTime.UtcNow;
        return msg with
        {
            Content = content ?? msg.Content,
            ContentFormat = contentFormat ?? msg.ContentFormat,
            ModifiedAt = now,
            Timestamp = msg.Timestamp ?? now
        };
    }

    public static long GetLatestContentTicks(IEnumerable<ChatMessage> messages)
    {
        long max = 0;
        foreach (var msg in messages)
        {
            if (msg.ModifiedAt.HasValue)
                max = Math.Max(max, msg.ModifiedAt.Value.Ticks);
            if (msg.Timestamp.HasValue)
                max = Math.Max(max, msg.Timestamp.Value.Ticks);
            if (msg.DeletedAt.HasValue)
                max = Math.Max(max, msg.DeletedAt.Value.Ticks);
        }

        return max;
    }

    public static string ResolveIncomingNoteTitle(string? incomingTitle, string? localTitle)
    {
        var incoming = string.IsNullOrWhiteSpace(incomingTitle) ? "Untitled" : incomingTitle.Trim();
        var local = localTitle?.Trim();

        if (string.IsNullOrWhiteSpace(local) || GenericNoteTitles.Contains(local))
            return incoming;

        if (GenericNoteTitles.Contains(incoming))
            return local;

        return incoming;
    }

    public static string ResolveIncomingConvoTitle(
        string? incomingTitle,
        string? localTitle,
        bool incomingTitleIsCustom,
        bool localTitleIsCustom)
    {
        if (incomingTitleIsCustom && !string.IsNullOrWhiteSpace(incomingTitle))
            return incomingTitle.Trim();

        if (localTitleIsCustom && !string.IsNullOrWhiteSpace(localTitle))
            return localTitle.Trim();

        var incoming = string.IsNullOrWhiteSpace(incomingTitle) ? "(empty)" : incomingTitle.Trim();
        var local = localTitle?.Trim();

        if (string.IsNullOrWhiteSpace(local) || GenericConvoTitles.Contains(local))
            return incoming;

        if (GenericConvoTitles.Contains(incoming))
            return local;

        return incoming;
    }
}