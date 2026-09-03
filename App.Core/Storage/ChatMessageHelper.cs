namespace App.Core.Storage;

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

    /// <summary>
    /// True when <paramref name="title"/> is empty, a generic placeholder, the item id,
    /// or a GUID (the catch-up path used to send notebook ids as titles).
    /// </summary>
    public static bool IsPlaceholderNoteTitle(string? title, string? itemId = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        var t = title.Trim();
        if (GenericNoteTitles.Contains(t))
            return true;

        if (!string.IsNullOrWhiteSpace(itemId)
            && string.Equals(t, itemId.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        return Guid.TryParse(t, out _);
    }

    /// <summary>
    /// Title to put on the wire. Never the item id.
    /// </summary>
    public static string ResolveOutgoingNoteTitle(string? title, string? itemId)
    {
        var t = title?.Trim();
        if (string.IsNullOrWhiteSpace(t) || IsPlaceholderNoteTitle(t, itemId))
            return "Untitled";
        return t;
    }

    /// <summary>
    /// Title to persist on a local save. Intentional rename keeps the requested string;
    /// content saves will not replace a real stored name with a placeholder.
    /// </summary>
    public static string ResolveLocalPersistTitle(
        string? requestedTitle,
        string? existingTitle,
        string? itemId,
        bool titleIsIntentionalRename)
    {
        var requested = requestedTitle?.Trim();
        var existing = existingTitle?.Trim();

        if (titleIsIntentionalRename)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return string.IsNullOrWhiteSpace(existing) ? "Untitled" : existing!;
            return requested;
        }

        if (string.IsNullOrWhiteSpace(requested) || IsPlaceholderNoteTitle(requested, itemId))
        {
            if (!string.IsNullOrWhiteSpace(existing) && !IsPlaceholderNoteTitle(existing, itemId))
                return existing!;
            return string.IsNullOrWhiteSpace(existing) ? "Untitled" : existing!;
        }

        return requested;
    }

    public static string ResolveIncomingNoteTitle(string? incomingTitle, string? localTitle, string? itemId = null)
    {
        var incoming = string.IsNullOrWhiteSpace(incomingTitle) ? "Untitled" : incomingTitle.Trim();
        var local = localTitle?.Trim();
        var incomingPlaceholder = IsPlaceholderNoteTitle(incoming, itemId);
        var localMissing = string.IsNullOrWhiteSpace(local) || IsPlaceholderNoteTitle(local, itemId);

        if (localMissing)
            return incomingPlaceholder
                ? (string.IsNullOrWhiteSpace(local) ? "Untitled" : local!)
                : incoming;

        if (incomingPlaceholder)
            return local!;

        return incoming;
    }

    /// <summary>
    /// Last-write-wins for notebook titles. Newer <paramref name="incomingTicks"/> wins;
    /// placeholder remote titles never replace a real local name; legacy peers (ticks 0)
    /// cannot overwrite a local title that has an intentional rename clock.
    /// </summary>
    public static bool TryResolveIncomingNoteTitle(
        string? incomingTitle,
        long? incomingTicks,
        string? localTitle,
        long localTicks,
        string? itemId,
        out string applyTitle,
        out long applyTicks)
    {
        var local = string.IsNullOrWhiteSpace(localTitle) ? null : localTitle.Trim();
        var localMissing = string.IsNullOrWhiteSpace(local) || IsPlaceholderNoteTitle(local, itemId);
        var incomingPlaceholder = IsPlaceholderNoteTitle(incomingTitle, itemId);
        var incoming = incomingPlaceholder
            ? "Untitled"
            : incomingTitle!.Trim();

        var rTicks = incomingTicks is > 0 ? incomingTicks.Value : 0L;
        var lTicks = localTicks > 0 ? localTicks : 0L;

        applyTitle = localMissing ? (local ?? "Untitled") : local!;
        applyTicks = lTicks;

        if (incomingPlaceholder)
        {
            if (localMissing && !string.Equals(applyTitle, incoming, StringComparison.Ordinal))
            {
                applyTitle = incoming;
                return true;
            }

            return false;
        }

        if (rTicks > lTicks)
        {
            applyTitle = incoming;
            applyTicks = rTicks;
            return true;
        }

        if (rTicks > 0)
            return false;

        // Legacy remote (no clock): keep a local name that was intentionally renamed.
        if (lTicks > 0 && !localMissing)
            return false;

        var resolved = ResolveIncomingNoteTitle(incomingTitle, localTitle, itemId);
        if (string.Equals(resolved, applyTitle, StringComparison.Ordinal))
            return false;

        applyTitle = resolved;
        applyTicks = 0;
        return true;
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