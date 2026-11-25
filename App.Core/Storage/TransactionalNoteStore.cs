using System.Globalization;
using System.Text.Json;
using App.Core.Auth;

namespace App.Core.Storage;

public sealed record NoteMutation(string? Title = null, List<ChatMessage>? Entries = null,
    long? TitleTicks = null, bool? Protected = null, long? ProtectionTicks = null,
    bool Remote = false, long? DeleteTicks = null, bool Repair = false);
public sealed record NoteCommit(bool Changed, string Fingerprint, NoteSyncPayload? Payload);

// Matches the existing IndexedDB record names. Exact clock strings also survive the JS
// number boundary, while the numeric fields remain readable by earlier releases.
public sealed record NoteHeader(string Key, string Id, string Namespace, string Title, string LastUpdated,
    bool SyncEnabled, string? ContentFingerprint, string? DeletedAt, bool? IsPasswordProtected = false,
    int? SortOrder = null, long? ProtectionChangedTicks = null, long? TitleChangedTicks = null,
    string? ExactProtection = null, string? ExactTitle = null)
{
    public long ProtectionClock() => long.TryParse(ExactProtection, out var n) ? n : ProtectionChangedTicks ?? 0;
    public long TitleClock() => long.TryParse(ExactTitle, out var n) ? n : TitleChangedTicks ?? 0;
}
public sealed record NoteSnapshot(NoteHeader? Meta, string? Content);
public sealed record NotebookDraft(string Session, ChatMessage Original, string Html);

public abstract class TransactionalNoteStore(IAuthService auth, ICryptoService crypto) : INoteStore
{
    private const string Verified = "v2:";
    protected abstract Task<List<NoteHeader>> HeadersAsync(string ns, CancellationToken ct);
    protected abstract Task<NoteSnapshot> ReadAsync(string key, CancellationToken ct);
    // Compare and write under one database transaction (including across browser tabs).
    protected abstract Task<bool> ReplaceAsync(NoteSnapshot expected, NoteHeader meta, string? content, CancellationToken ct);
    protected abstract Task SetOrderAsync(string key, int order, CancellationToken ct);
    protected abstract Task RemoveDraftRowAsync(string key, CancellationToken ct);
    private string Namespace => StorageNamespace.GetPrefix(auth);
    private static string Key(string ns, string id) => ns + "n-wasmchat-note-" + id;
    private string DraftNamespace(string id) => Namespace + "note-drafts:" + id + ":";
    public async Task<List<NotebookDraft>> LoadDraftsAsync(string id, CancellationToken ct = default)
    {
        var drafts = new List<NotebookDraft>();
        foreach (var m in (await HeadersAsync(DraftNamespace(id), ct)).OrderByDescending(h => h.LastUpdated))
        {
            var row = await ReadAsync(m.Key, ct);
            if (row.Content != null) drafts.Add(JsonSerializer.Deserialize<NotebookDraft>(await crypto.DecryptAsync(
                await auth.GetOrCreateHistoryEncryptionKeyAsync(), row.Content, ct)) ?? throw new InvalidDataException("Invalid note draft."));
        }
        return drafts;
    }
    public async Task WriteDraftAsync(string id, NotebookDraft draft, CancellationToken ct = default)
    {
        var ns = DraftNamespace(id);
        var json = JsonSerializer.Serialize(draft);
        var key = await auth.GetOrCreateHistoryEncryptionKeyAsync();
        var cipher = await crypto.EncryptAsync(key, json, ct);
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(cipher) || json == cipher) throw new InvalidOperationException("Could not encrypt draft.");
        var meta = new NoteHeader(ns + draft.Session, draft.Session, ns, "Draft", DateTime.UtcNow.ToString("o"), false, null, null);
        if (!await ReplaceAsync(await ReadAsync(meta.Key, ct), meta, cipher, ct)) throw new IOException("Draft changed during save.");
    }
    public Task ClearDraftAsync(string id, string session, CancellationToken ct = default) => RemoveDraftRowAsync(DraftNamespace(id) + session, ct);
    private static DateTime Utc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static string WireFingerprint(NoteHeader meta) => meta.ContentFingerprint is {} fp && fp.StartsWith(Verified, StringComparison.Ordinal) ? fp[Verified.Length..] : meta.ContentFingerprint ?? "";

    public async Task<List<LocalNote>> LoadIndexAsync(CancellationToken ct = default) =>
        (await HeadersAsync(Namespace, ct)).Where(m => string.IsNullOrEmpty(m.DeletedAt))
        .OrderBy(m => m.SortOrder ?? 0).ThenByDescending(m => m.LastUpdated, StringComparer.Ordinal)
        .Select(m => new LocalNote(m.Id, m.Title, Utc(m.LastUpdated), m.IsPasswordProtected == true,
            m.SortOrder ?? 0, m.ProtectionClock(), m.TitleClock(), WireFingerprint(m))).ToList();

    public async Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var headers = await HeadersAsync(Namespace, ct);
        var list = new List<SyncManifestEntry>(headers.Count);
        foreach (var header in headers)
        {
            var m = header;
            if (backfillMissingFingerprints && string.IsNullOrEmpty(m.DeletedAt) && m.ContentFingerprint?.StartsWith(Verified, StringComparison.Ordinal) != true)
            {
                await CommitAsync(m.Id, new(Repair: true), ct);
                m = (await ReadAsync(m.Key, ct)).Meta ?? m;
            }
            list.Add(new(m.Id, m.Title, Utc(m.LastUpdated).Ticks, WireFingerprint(m),
                string.IsNullOrEmpty(m.DeletedAt) ? null : Utc(m.DeletedAt).Ticks));
        }
        return list;
    }

    private async Task<List<ChatMessage>> DecodeAsync(NoteSnapshot row, CancellationToken ct)
    {
        if (row.Content is null && (row.Meta == null || !string.IsNullOrEmpty(row.Meta.DeletedAt))) return [];
        if (string.IsNullOrEmpty(row.Content)) throw new InvalidDataException("Notebook body is missing.");
        var json = await crypto.DecryptAsync(await auth.GetOrCreateHistoryEncryptionKeyAsync(), row.Content, ct);
        return NoteSyncMerger.Normalize(JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? throw new InvalidDataException("Invalid notebook body."));
    }

    public async Task<List<ChatMessage>> LoadNoteAsync(string id, CancellationToken ct = default) =>
        await DecodeAsync(await ReadAsync(Key(Namespace, id), ct), ct);
    public async Task<string?> GetMetaTitleAsync(string id, CancellationToken ct = default) => (await ReadAsync(Key(Namespace, id), ct)).Meta?.Title;
    public async Task<NoteSyncPayload?> ReadPayloadAsync(string id, CancellationToken ct = default)
    {
        var row = await ReadAsync(Key(Namespace, id), ct);
        var m = row.Meta;
        return m == null || !string.IsNullOrEmpty(m.DeletedAt) ? null : Payload(m, await DecodeAsync(row, ct));
    }
    private static NoteSyncPayload Payload(NoteHeader m, List<ChatMessage> entries) => new(m.Id, m.Title, entries,
        m.IsPasswordProtected == true, m.ProtectionClock() == 0 ? null : m.ProtectionClock(), m.TitleClock() == 0 ? null : m.TitleClock());

    public async Task<NoteCommit> CommitAsync(string id, NoteMutation mutation, CancellationToken ct = default)
    {
        var ns = Namespace;
        var key = Key(ns, id);
        var incoming = mutation.Entries == null ? null : NoteSyncMerger.Normalize(mutation.Entries);
        var now = DateTime.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (ns != Namespace) throw new InvalidOperationException("Notebook user changed during save.");
            var row = await ReadAsync(key, ct);
            var old = row.Meta;
            var m = old ?? new NoteHeader(key, id, ns, mutation.Title ?? "Untitled", now.ToString("o"), auth.IsAuthenticated, null, null,
                SortOrder: (await HeadersAsync(ns, ct)).Select(x => x.SortOrder ?? 0).DefaultIfEmpty(-1).Max() + 1);
            var deleted = string.IsNullOrEmpty(m.DeletedAt) ? 0 : Utc(m.DeletedAt).Ticks;
            if (mutation.DeleteTicks is long d)
            {
                if (deleted >= d) return new(false, WireFingerprint(m), null);
                if (mutation.Remote && deleted == 0 && old != null)
                {
                    var existing = await DecodeAsync(row, ct);
                    if (Math.Max(ChatMessageHelper.GetLatestContentTicks(existing), Math.Max(m.TitleClock(), m.ProtectionClock())) > d)
                        return new(false, SyncFingerprint.ForNote(id, m.Title, existing, m.IsPasswordProtected == true, m.ProtectionClock(), m.TitleClock()), Payload(m, existing));
                }
                var stamp = new DateTime(d, DateTimeKind.Utc).ToString("o");
                var tombstone = m with { DeletedAt = stamp, LastUpdated = stamp, ContentFingerprint = DeleteSyncPayload.AckValue(d) };
                if (await ReplaceAsync(row, tombstone, null, ct)) return new(true, tombstone.ContentFingerprint!, null);
                continue;
            }
            if (deleted != 0 && (incoming == null || ChatMessageHelper.GetLatestContentTicks(incoming) <= deleted))
            {
                if (!mutation.Remote && !mutation.Repair) throw new InvalidOperationException("Notebook was deleted before the edit was saved.");
                return new(false, WireFingerprint(m), null);
            }
            var local = await DecodeAsync(row, ct);
            var entries = incoming == null ? local : NoteSyncMerger.MergeEntries(local, incoming, partial: !mutation.Remote);
            var title = m.Title;
            var titleClock = m.TitleClock();
            if (mutation.Remote && old != null)
                ChatMessageHelper.TryResolveIncomingNoteTitle(mutation.Title, mutation.TitleTicks, m.Title, titleClock, id, out title, out titleClock);
            else if (mutation.Remote) titleClock = mutation.TitleTicks ?? 0;
            else if (mutation.TitleTicks.HasValue && (old == null || mutation.Title != title))
                (title, titleClock) = (mutation.Title ?? title, mutation.TitleTicks.Value);
            var locked = m.IsPasswordProtected == true;
            var lockClock = m.ProtectionClock();
            if (mutation.Remote)
                PasswordProtectionSync.TryResolve(mutation.Protected, mutation.ProtectionTicks, locked, lockClock, out locked, out lockClock);
            else if (mutation.Protected is bool flag && flag != locked)
                (locked, lockClock) = (flag, mutation.ProtectionTicks ?? now.Ticks);
            var fp = SyncFingerprint.ForNote(id, title, entries, locked, lockClock, titleClock);
            var changed = old == null || deleted != 0 || SyncFingerprint.ForNote(id, m.Title, local, m.IsPasswordProtected == true, m.ProtectionClock(), m.TitleClock()) != fp;
            var next = m with { Title = title, TitleChangedTicks = titleClock, ExactTitle = titleClock.ToString(),
                IsPasswordProtected = locked, ProtectionChangedTicks = lockClock, ExactProtection = lockClock.ToString(),
                LastUpdated = changed ? now.ToString("o") : m.LastUpdated, DeletedAt = null, ContentFingerprint = Verified + fp };
            if (changed || m.ContentFingerprint != next.ContentFingerprint)
            {
                var json = JsonSerializer.Serialize(entries);
                var secret = await auth.GetOrCreateHistoryEncryptionKeyAsync();
                var cipher = await crypto.EncryptAsync(secret, json, ct);
                if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(cipher) || cipher == json)
                    throw new InvalidOperationException("Could not encrypt notebook.");
                if (ns != Namespace) throw new InvalidOperationException("Notebook user changed during save.");
                if (!await ReplaceAsync(row, next, cipher, ct)) continue;
            }
            return new(changed, fp, Payload(next, entries));
        }
    }
    public virtual async Task<DateTime> DeleteNoteAsync(string id, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await CommitAsync(id, new(DeleteTicks: now.Ticks), ct);
        return now;
    }
    public async Task<bool> TryApplyRemoteDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default) =>
        (await CommitAsync(id, new(Remote: true, DeleteTicks: deletedAtTicks), ct)).Changed;
    public async Task SetPasswordProtectedAsync(string id, bool isProtected, long? protectionChangedTicks = null, CancellationToken ct = default) =>
        await CommitAsync(id, new(Protected: isProtected, ProtectionTicks: protectionChangedTicks), ct);
    public async Task ReorderNotesAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        var ns = Namespace;
        for (int i = 0; i < orderedIds.Count; i++) await SetOrderAsync(Key(ns, orderedIds[i]), i, ct);
    }
}
