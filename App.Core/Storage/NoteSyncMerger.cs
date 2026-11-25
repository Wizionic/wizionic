using System.Text.Json;

namespace App.Core.Storage;

public static class NoteSyncMerger
{
    // Stable on old notebooks too: inspecting the same legacy body on two devices must
    // not manufacture two different entry IDs, timestamps, or manifest fingerprints.
    public static List<ChatMessage> Normalize(IEnumerable<ChatMessage> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return entries.Select((entry, i) =>
        {
            var id = entry.ItemId;
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
            {
                id = "legacy-" + SyncFingerprint.Compute($"{i}:{JsonSerializer.Serialize(entry, SyncJson.Options)}");
                seen.Add(id);
            }
            return entry with { ItemId = id, Timestamp = entry.Timestamp ?? entry.ModifiedAt ?? DateTime.UnixEpoch };
        }).ToList();
    }

    private static string Version(ChatMessage m) => SyncFingerprint.Compute(JsonSerializer.Serialize(
        m with { NoteAncestors = null, NotePosition = null, NotePositionClock = null }, SyncJson.Options));

    public static ChatMessage Edited(ChatMessage original, ChatMessage edit) => edit with
    {
        NoteAncestors = Version(original) == Version(edit) ? original.NoteAncestors : Ancestors(original.NoteAncestors, [Version(original)])
    };

    private static List<string>? Ancestors(IEnumerable<string>? a, IEnumerable<string>? b)
    {
        var ids = (a ?? []).Union(b ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return ids.Count == 0 ? null : ids;
    }

    public static List<ChatMessage> MergeEntries(IReadOnlyList<ChatMessage> local, IReadOnlyList<ChatMessage> remote, bool partial = false)
    {
        var rows = new Dictionary<string, ChatMessage>(StringComparer.OrdinalIgnoreCase);
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var recovered = new List<ChatMessage>();
        void Add(ChatMessage m, int index)
        {
            var id = m.ItemId!;
            positions[id] = Math.Min(positions.GetValueOrDefault(id, int.MaxValue), index);
            if (!rows.TryGetValue(id, out var l)) { rows[id] = m; return; }
            if (l == m) return;
            var lv = Version(l);
            var rv = Version(m);
            var localDescends = l.NoteAncestors?.Contains(rv) == true;
            var remoteDescends = m.NoteAncestors?.Contains(lv) == true;
            var tickOrder = EffectiveTime(l).CompareTo(EffectiveTime(m));
            var winner = localDescends != remoteDescends ? (localDescends ? l : m)
                : tickOrder != 0 ? (tickOrder > 0 ? l : m) : string.CompareOrdinal(lv, rv) >= 0 ? l : m;
            var loser = ReferenceEquals(winner, l) ? m : l;
            var losingVersion = ReferenceEquals(winner, l) ? rv : lv;
            var ancestry = Ancestors(l.NoteAncestors, m.NoteAncestors);
            if (lv != rv && !localDescends && !remoteDescends)
            {
                // Clocks order the display; they cannot prove that the winner ever saw the loser.
                // Retain differing live content with a stable ID so retries don't make copies.
                if (loser.DeletedAt == null && (winner.DeletedAt != null || !SameBody(winner, loser)))
                    recovered.Add(loser with { ItemId = $"{id}-recovered-{losingVersion}", NoteAncestors = null });
                ancestry = Ancestors(ancestry, [losingVersion]);
            }
            var position = (l.NotePositionClock ?? 0, l.NotePosition ?? index).CompareTo(
                (m.NotePositionClock ?? 0, m.NotePosition ?? index)) >= 0 ? l : m;
            rows[id] = winner with { NoteAncestors = ancestry, NotePosition = position.NotePosition, NotePositionClock = position.NotePositionClock };
        }
        for (int i = 0; i < local.Count; i++) Add(local[i], i);
        for (int i = 0; i < remote.Count; i++) Add(remote[i], partial ? positions.GetValueOrDefault(remote[i].ItemId!, local.Count + i) : i);
        foreach (var m in recovered) if (!rows.ContainsKey(m.ItemId!)) rows[m.ItemId!] = m;
        return rows.Values.OrderBy(m => m.NotePosition ?? positions.GetValueOrDefault(m.ItemId!, int.MaxValue))
            .ThenBy(m => m.ItemId, StringComparer.Ordinal).ToList();
    }

    private static bool SameBody(ChatMessage a, ChatMessage b) => JsonSerializer.Serialize(a with
    {
        ModifiedAt = b.ModifiedAt, NoteAncestors = b.NoteAncestors, NotePosition = b.NotePosition,
        NotePositionClock = b.NotePositionClock
    }, SyncJson.Options) == JsonSerializer.Serialize(b, SyncJson.Options);

    public static DateTime EffectiveTime(ChatMessage m) => new[] { m.Timestamp, m.ModifiedAt, m.DeletedAt }.Max() ?? DateTime.UnixEpoch;
}
