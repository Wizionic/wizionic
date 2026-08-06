namespace App.Core.Storage;

/// <summary>
/// Merges two versions of a notebook (list of note entries) instead of whole-note overwrite.
/// Entries are matched by <see cref="ChatMessage.ItemId"/>; each id keeps the version with the
/// newer effective timestamp (ModifiedAt / DeletedAt / Timestamp). Local list order is preserved;
/// remote-only entries are appended in remote order.
/// </summary>
public static class NoteSyncMerger
{
    public sealed record MergeResult(
        List<ChatMessage> Entries,
        bool DiffersFromLocal,
        bool DiffersFromRemote,
        int RemoteOnlyCount,
        int LocalOnlyCount,
        int ResolvedConflicts);

    /// <summary>
    /// Union of local and remote entries by ItemId with last-write-wins on each entry.
    /// </summary>
    public static MergeResult Merge(
        IReadOnlyList<ChatMessage>? localEntries,
        IReadOnlyList<ChatMessage>? remoteEntries)
    {
        var local = ChatMessageHelper.NormalizeAll(localEntries ?? Array.Empty<ChatMessage>());
        var remote = ChatMessageHelper.NormalizeAll(remoteEntries ?? Array.Empty<ChatMessage>());

        if (local.Count == 0)
        {
            return new MergeResult(
                remote,
                DiffersFromLocal: remote.Count > 0,
                DiffersFromRemote: false,
                RemoteOnlyCount: remote.Count,
                LocalOnlyCount: 0,
                ResolvedConflicts: 0);
        }

        if (remote.Count == 0)
        {
            return new MergeResult(
                local,
                DiffersFromLocal: false,
                DiffersFromRemote: local.Count > 0,
                RemoteOnlyCount: 0,
                LocalOnlyCount: local.Count,
                ResolvedConflicts: 0);
        }

        var localById = IndexByItemId(local);
        var remoteById = IndexByItemId(remote);

        var chosen = new Dictionary<string, ChatMessage>(StringComparer.OrdinalIgnoreCase);
        var remoteOnly = 0;
        var localOnly = 0;
        var resolvedConflicts = 0;

        foreach (var id in localById.Keys.Union(remoteById.Keys, StringComparer.OrdinalIgnoreCase))
        {
            localById.TryGetValue(id, out var l);
            remoteById.TryGetValue(id, out var r);

            if (l is null)
            {
                chosen[id] = r!;
                remoteOnly++;
                continue;
            }

            if (r is null)
            {
                chosen[id] = l;
                localOnly++;
                continue;
            }

            if (EntryContentEquals(l, r))
            {
                // Prefer the side with the newer clock so metadata stays consistent.
                chosen[id] = EffectiveTicks(r) > EffectiveTicks(l) ? r : l;
                continue;
            }

            resolvedConflicts++;
            chosen[id] = PickNewer(l, r);
        }

        var result = new List<ChatMessage>(chosen.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Preserve local notebook order for entries that exist locally.
        foreach (var entry in local)
        {
            var id = entry.ItemId!;
            if (!chosen.TryGetValue(id, out var picked))
                continue;
            if (!seen.Add(id))
                continue;
            result.Add(picked);
        }

        // Append remote-only entries in the order the peer had them.
        foreach (var entry in remote)
        {
            var id = entry.ItemId!;
            if (!seen.Add(id))
                continue;
            if (!chosen.TryGetValue(id, out var picked))
                continue;
            result.Add(picked);
        }

        var differsFromLocal = !EntryListsEquivalent(local, result);
        var differsFromRemote = !EntryListsEquivalent(remote, result);

        return new MergeResult(
            result,
            differsFromLocal,
            differsFromRemote,
            remoteOnly,
            localOnly,
            resolvedConflicts);
    }

    /// <summary>
    /// Same as <see cref="Merge"/> but returns only the entry list.
    /// </summary>
    public static List<ChatMessage> MergeEntries(
        IReadOnlyList<ChatMessage>? localEntries,
        IReadOnlyList<ChatMessage>? remoteEntries) =>
        Merge(localEntries, remoteEntries).Entries;

    private static Dictionary<string, ChatMessage> IndexByItemId(List<ChatMessage> entries)
    {
        var map = new Dictionary<string, ChatMessage>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ItemId))
                continue;
            // Last occurrence wins if a list somehow has duplicate ids.
            map[entry.ItemId] = entry;
        }

        return map;
    }

    private static ChatMessage PickNewer(ChatMessage a, ChatMessage b)
    {
        var ta = EffectiveTicks(a);
        var tb = EffectiveTicks(b);
        if (tb > ta)
            return b;
        if (ta > tb)
            return a;

        // Clock tie: prefer delete over live so deletes converge; then stable content order.
        if (a.DeletedAt.HasValue != b.DeletedAt.HasValue)
            return a.DeletedAt.HasValue ? a : b;

        var cmp = string.CompareOrdinal(a.Content ?? "", b.Content ?? "");
        return cmp >= 0 ? a : b;
    }

    private static long EffectiveTicks(ChatMessage msg)
    {
        long max = 0;
        if (msg.ModifiedAt.HasValue)
            max = Math.Max(max, msg.ModifiedAt.Value.Ticks);
        if (msg.DeletedAt.HasValue)
            max = Math.Max(max, msg.DeletedAt.Value.Ticks);
        if (msg.Timestamp.HasValue)
            max = Math.Max(max, msg.Timestamp.Value.Ticks);
        return max;
    }

    private static bool EntryContentEquals(ChatMessage a, ChatMessage b)
    {
        if (a.DeletedAt.HasValue != b.DeletedAt.HasValue)
            return false;
        if (!string.Equals(a.Content ?? "", b.Content ?? "", StringComparison.Ordinal))
            return false;
        if (!string.Equals(a.ContentFormat ?? "", b.ContentFormat ?? "", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool EntryListsEquivalent(List<ChatMessage> a, List<ChatMessage> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].ItemId, b[i].ItemId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!EntryContentEquals(a[i], b[i]))
                return false;
        }

        return true;
    }
}
