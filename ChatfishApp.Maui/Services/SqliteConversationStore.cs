using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Storage;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatfishApp.Maui.Services;

public class SqliteConversationStore : IConversationStore
{
    private const string LastConvoKey = "wasmchat-last-convo";
    private const string ConvoPrefix = "wasmchat-convo-";

    private readonly IAuthService _auth;
    private readonly ICryptoService _crypto;
    private readonly SqliteHistoryDatabase _db;

    public SqliteConversationStore(IAuthService auth, ICryptoService crypto, SqliteHistoryDatabase db)
    {
        _auth = auth;
        _crypto = crypto;
        _db = db;
    }

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private async Task<string> GetHistoryKeyAsync() =>
        await _auth.GetOrCreateHistoryEncryptionKeyAsync();

    private async Task<string?> ReadContentAsync(string storageKey, CancellationToken ct)
    {
        var keyB64 = await GetHistoryKeyAsync();
        var stored = await _db.GetConvoContentAsync(storageKey, ct);
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(keyB64))
            return stored;

        return await _crypto.DecryptAsync(keyB64, stored, ct);
    }

    private async Task WriteContentAsync(string storageKey, string json, CancellationToken ct)
    {
        var keyB64 = await GetHistoryKeyAsync();
        var toStore = json;
        if (!string.IsNullOrEmpty(keyB64))
            toStore = await _crypto.EncryptAsync(keyB64, json, ct);

        await _db.SetConvoContentAsync(storageKey, toStore, ct);
    }

    private static bool HasCustomTitle(SqliteHistoryDatabase.ConvoMetaRow? meta) =>
        meta?.TitleIsCustom == true;

    public async Task<List<LocalConvo>> LoadIndexAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetConvoMetasByNamespaceAsync(ns, ct);
        var live = metas.Where(m => string.IsNullOrEmpty(m.DeletedAt)).ToList();

        if (live.Count > 0 && live.All(m => m.SortOrder == 0))
        {
            var ordered = live.OrderByDescending(m => m.LastUpdated).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i] with { SortOrder = i };
                await _db.UpsertConvoMetaAsync(m, ct);
                ordered[i] = m;
            }
            live = ordered;
        }

        return live
            .OrderBy(m => m.SortOrder)
            .ThenByDescending(m => m.LastUpdated)
            .Select(m => new LocalConvo(m.Id, m.Title, DateTime.Parse(m.LastUpdated), m.SortOrder))
            .ToList();
    }

    public async Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetConvoMetasByNamespaceAsync(ns, ct);
        var entries = new List<SyncManifestEntry>();

        foreach (var m in metas)
        {
            var title = string.IsNullOrWhiteSpace(m.Title) ? "(empty)" : m.Title;
            long? deletedAtTicks = null;
            if (!string.IsNullOrEmpty(m.DeletedAt))
                deletedAtTicks = DateTime.Parse(m.DeletedAt).Ticks;

            var fingerprint = deletedAtTicks.HasValue
                ? DeleteSyncPayload.AckValue(deletedAtTicks.Value)
                : m.ContentFingerprint ?? "";

            if (!deletedAtTicks.HasValue && backfillMissingFingerprints && string.IsNullOrEmpty(fingerprint))
            {
                var messages = await LoadConversationAsync(m.Id, ct);
                fingerprint = SyncFingerprint.ForConversation(m.Id, title, messages);
                await _db.UpsertConvoMetaAsync(m with { ContentFingerprint = fingerprint }, ct);
            }

            entries.Add(new SyncManifestEntry(
                m.Id,
                title,
                DateTime.Parse(m.LastUpdated).Ticks,
                fingerprint,
                deletedAtTicks));
        }

        return entries;
    }

    public async Task<List<ChatMessage>> LoadConversationAsync(string id, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var storageKey = ns + ConvoPrefix + id;
        var json = await ReadContentAsync(storageKey, ct);
        if (string.IsNullOrEmpty(json))
            return new List<ChatMessage>();

        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new();
        return ChatMessageHelper.NormalizeAll(messages);
    }

    public async Task SaveConversationAsync(string id, List<ChatMessage> messages, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var storageKey = ns + ConvoPrefix + id;
        var json = JsonSerializer.Serialize(messages);
        await WriteContentAsync(storageKey, json, ct);
    }

    public async Task<DateTime> DeleteConversationAsync(string id, CancellationToken ct = default)
    {
        var deletedAt = DateTime.UtcNow;
        var deletedAtIso = deletedAt.ToString("o");
        var ns = GetPrefix();
        var storageKey = ns + ConvoPrefix + id;
        var existing = await _db.GetConvoMetaByIdAsync(ns, id, ct);
        var title = existing?.Title ?? "(deleted)";
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);

        await _db.UpsertConvoMetaAsync(new SqliteHistoryDatabase.ConvoMetaRow(
            existing?.StorageKey ?? storageKey,
            id,
            ns,
            title,
            deletedAtIso,
            syncEnabled,
            DeleteSyncPayload.AckValue(deletedAt.Ticks),
            deletedAtIso,
            existing?.TitleIsCustom ?? false,
            existing?.SortOrder ?? 0), ct);

        await _db.DeleteConvoContentAsync(existing?.StorageKey ?? storageKey, ct);
        return deletedAt;
    }

    public async Task<string?> GetMetaTitleAsync(string id, CancellationToken ct = default)
    {
        var meta = await _db.GetConvoMetaByIdAsync(GetPrefix(), id, ct);
        return meta?.Title;
    }

    public async Task<(string Title, bool TitleIsCustom)> GetMetaTitleInfoAsync(string id, CancellationToken ct = default)
    {
        var meta = await _db.GetConvoMetaByIdAsync(GetPrefix(), id, ct);
        var title = string.IsNullOrWhiteSpace(meta?.Title) ? "(empty)" : meta.Title;
        return (title, HasCustomTitle(meta));
    }

    public async Task SetConversationTitleAsync(string id, string title, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var storageKey = ns + ConvoPrefix + id;
        var existing = await _db.GetConvoMetaByIdAsync(ns, id, ct);
        var normalizedTitle = NormalizeCustomTitle(title);
        var now = DateTime.UtcNow.ToString("o");
        bool syncEnabled = existing?.SyncEnabled
            ?? (_auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email));
        var messages = await LoadConversationAsync(id, ct);
        var contentFingerprint = SyncFingerprint.ForConversation(id, normalizedTitle, messages);

        await _db.UpsertConvoMetaAsync(new SqliteHistoryDatabase.ConvoMetaRow(
            existing?.StorageKey ?? storageKey,
            id,
            ns,
            normalizedTitle,
            now,
            syncEnabled,
            contentFingerprint,
            existing?.DeletedAt,
            true,
            existing?.SortOrder ?? 0), ct);
    }

    public async Task UpdateIndexAfterSaveAsync(string id, List<ChatMessage> messages, List<LocalConvo> currentIndex, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var storageKey = ns + ConvoPrefix + id;
        var existing = await _db.GetConvoMetaByIdAsync(ns, id, ct);

        string title;
        bool titleIsCustom;
        if (HasCustomTitle(existing))
        {
            title = string.IsNullOrWhiteSpace(existing!.Title) ? "(empty)" : existing.Title;
            titleIsCustom = true;
        }
        else
        {
            var raw = messages.FirstOrDefault(m => ChatMessageHelper.IsVisible(m) && (m.Role == "user" || m.User == "LocalUser"))?.Content
                      ?? messages.FirstOrDefault(m => ChatMessageHelper.IsVisible(m))?.Content;
            title = string.IsNullOrWhiteSpace(raw) ? "(empty)" : StripHtmlForTitle(raw);
            titleIsCustom = false;
        }

        var now = DateTime.UtcNow.ToString("o");
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);
        var contentFingerprint = SyncFingerprint.ForConversation(id, title, messages);

        int sortOrder;
        if (existing is null)
        {
            var known = currentIndex.FirstOrDefault(c => c.Id == id);
            if (known is not null)
                sortOrder = known.SortOrder;
            else if (currentIndex.Count > 0)
                // New chats appear at the top without reshuffling existing SortOrders.
                sortOrder = currentIndex.Min(c => c.SortOrder) - 1;
            else
                sortOrder = 0;
        }
        else
        {
            sortOrder = existing.SortOrder;
        }

        await _db.UpsertConvoMetaAsync(new SqliteHistoryDatabase.ConvoMetaRow(
            existing?.StorageKey ?? storageKey,
            id,
            ns,
            title,
            now,
            syncEnabled,
            contentFingerprint,
            existing?.DeletedAt,
            titleIsCustom,
            sortOrder), ct);

        await SetLastConvoIdAsync(id, ct);
    }

    public async Task<string?> GetLastConvoIdAsync(CancellationToken ct = default)
    {
        var settingKey = GetPrefix() + LastConvoKey;
        return await _db.GetSettingAsync(settingKey, ct);
    }

    public async Task SetLastConvoIdAsync(string id, CancellationToken ct = default)
    {
        var settingKey = GetPrefix() + LastConvoKey;
        await _db.SetSettingAsync(settingKey, id, ct);
    }

    public async Task<bool> ShouldAcceptIncomingContentAsync(string id, List<ChatMessage> messages, CancellationToken ct = default)
    {
        var meta = await _db.GetConvoMetaByIdAsync(GetPrefix(), id, ct);
        if (meta == null || string.IsNullOrEmpty(meta.DeletedAt))
            return true;

        var deletedAtTicks = DateTime.Parse(meta.DeletedAt).Ticks;
        return ChatMessageHelper.GetLatestContentTicks(messages) > deletedAtTicks;
    }

    public async Task<bool> TryApplyRemoteDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default)
    {
        var remoteDeletedAt = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        var meta = await _db.GetConvoMetaByIdAsync(GetPrefix(), id, ct);
        if (meta == null)
            return false;

        if (!string.IsNullOrEmpty(meta.DeletedAt))
        {
            var localDeletedAt = DateTime.Parse(meta.DeletedAt);
            if (localDeletedAt.Ticks >= deletedAtTicks)
                return false;
        }
        else
        {
            var contentTicks = ChatMessageHelper.GetLatestContentTicks(await LoadConversationAsync(id, ct));
            if (contentTicks > deletedAtTicks || DateTime.Parse(meta.LastUpdated).Ticks > deletedAtTicks)
                return false;
        }

        await DeleteConversationAsync(id, ct);
        return true;
    }

    public async Task SetConversationSyncEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var storageKey = ns + ConvoPrefix + id;
        var existing = await _db.GetConvoMetaByIdAsync(ns, id, ct);
        var now = existing?.LastUpdated ?? DateTime.UtcNow.ToString("o");

        await _db.UpsertConvoMetaAsync(new SqliteHistoryDatabase.ConvoMetaRow(
            storageKey,
            id,
            ns,
            existing?.Title ?? "(empty)",
            now,
            enabled,
            existing?.ContentFingerprint,
            existing?.DeletedAt,
            existing?.TitleIsCustom ?? false,
            existing?.SortOrder ?? 0), ct);
    }

    public async Task ReorderConversationsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds.Count == 0)
            return;

        var ns = GetPrefix();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var existing = await _db.GetConvoMetaByIdAsync(ns, orderedIds[i], ct);
            if (existing == null)
                continue;
            await _db.UpsertConvoMetaAsync(existing with { SortOrder = i }, ct);
        }
    }

    private static string StripHtmlForTitle(string htmlOrText)
    {
        if (string.IsNullOrWhiteSpace(htmlOrText)) return "(empty)";
        var plain = Regex.Replace(htmlOrText, "<.*?>", string.Empty);
        plain = WebUtility.HtmlDecode(plain).Trim();
        if (plain.Length > 30) plain = plain[..30] + "...";
        return plain;
    }

    private static string NormalizeCustomTitle(string title)
    {
        var trimmed = title.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "(empty)";
        return trimmed.Length > 80 ? trimmed[..80] : trimmed;
    }
}