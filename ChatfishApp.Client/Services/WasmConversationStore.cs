using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Storage;
using Microsoft.JSInterop;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Client-side storage for conversation history (multi-convo, titles, messages).
/// IndexedDB-backed; content blobs are AES-GCM encrypted at rest.
/// </summary>
public class WasmConversationStore : IConversationStore
{
    private const string LastConvoKey = "wasmchat-last-convo";
    private const string ConvoPrefix = "wasmchat-convo-";

    private readonly IAuthService _auth;
    private readonly ICryptoService _crypto;
    private readonly IJSRuntime _js;

    public WasmConversationStore(IAuthService auth, ICryptoService crypto, IJSRuntime js)
    {
        _auth = auth;
        _crypto = crypto;
        _js = js;
        _auth.OnChanged += () => { /* consumer decides when to reload */ };
    }

    private record StoredMeta(string key, string id, string @namespace, string title, string lastUpdated, bool syncEnabled, string? contentFingerprint, string? deletedAt, bool? titleIsCustom);

    private static bool HasCustomTitle(StoredMeta? meta) => meta?.titleIsCustom == true;

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private async Task<string> GetHistoryKeyAsync() =>
        await _auth.GetOrCreateHistoryEncryptionKeyAsync();

    private async Task<string?> ReadContentAsync(string fullKey)
    {
        var keyB64 = await GetHistoryKeyAsync();
        var stored = await _js.InvokeAsync<string>("idbGetContent", fullKey);
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(keyB64))
            return stored;

        return await _crypto.DecryptAsync(keyB64, stored);
    }

    private async Task WriteContentAsync(string fullKey, string json)
    {
        var keyB64 = await GetHistoryKeyAsync();
        var toStore = json;
        if (!string.IsNullOrEmpty(keyB64))
            toStore = await _crypto.EncryptAsync(keyB64, json);

        await _js.InvokeVoidAsync("idbPutContent", fullKey, toStore);
    }

    public async Task<List<LocalConvo>> LoadIndexAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredMeta>>("idbGetMetasByNamespace", ns);

        if (_auth.IsAuthenticated)
        {
            try
            {
                var allMetas = await _js.InvokeAsync<List<StoredMeta>>("idbGetAllMetas");
                var legacy = allMetas
                    .Where(m => !string.IsNullOrEmpty(m.@namespace) &&
                                (m.@namespace.StartsWith("wasmchat-") || m.@namespace.StartsWith("e-")) &&
                                m.@namespace != ns)
                    .ToList();

                var currentIds = metas.Select(m => m.id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var lm in legacy)
                {
                    if (!currentIds.Contains(lm.id))
                    {
                        metas.Add(lm);
                        currentIds.Add(lm.id);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WasmConvStore] Legacy meta recovery skipped: {ex.Message}");
            }
        }

        return metas
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .OrderByDescending(m => m.lastUpdated)
            .Select(m => new LocalConvo(m.id, m.title ?? "(empty)", DateTime.Parse(m.lastUpdated)))
            .ToList();
    }

    public async Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredMeta>>("idbGetMetasByNamespace", ns);
        var entries = new List<SyncManifestEntry>();

        foreach (var m in metas)
        {
            var title = string.IsNullOrWhiteSpace(m.title) ? "(empty)" : m.title;
            long? deletedAtTicks = null;
            if (!string.IsNullOrEmpty(m.deletedAt))
                deletedAtTicks = DateTime.Parse(m.deletedAt).Ticks;

            var fingerprint = deletedAtTicks.HasValue
                ? DeleteSyncPayload.AckValue(deletedAtTicks.Value)
                : m.contentFingerprint ?? "";

            if (!deletedAtTicks.HasValue)
            {
                var messages = await LoadConversationAsync(m.id, ct);
                var computed = SyncFingerprint.ForConversation(m.id, title, messages);
                if (!string.IsNullOrEmpty(fingerprint) && !string.Equals(fingerprint, computed, StringComparison.Ordinal))
                {
                    Console.WriteLine(
                        $"[WasmConvStore] Convo {m.id}: stored fingerprint does not match readable content " +
                        "(likely decrypt failure after key rotation); manifest will request resync");
                    fingerprint = computed;
                }
                else if (string.IsNullOrEmpty(fingerprint) && backfillMissingFingerprints)
                {
                    fingerprint = computed;
                    await PersistContentFingerprintAsync(m, title, fingerprint, deletedAt: null);
                }
            }

            entries.Add(new SyncManifestEntry(
                m.id,
                title,
                DateTime.Parse(m.lastUpdated).Ticks,
                fingerprint,
                deletedAtTicks));
        }

        return entries;
    }

    private async Task<StoredMeta?> GetMetaByIdAsync(string id)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredMeta>>("idbGetMetasByNamespace", ns);
        var meta = metas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
        if (meta != null)
            return meta;

        try
        {
            var allMetas = await _js.InvokeAsync<List<StoredMeta>>("idbGetAllMetas");
            return allMetas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private async Task PersistContentFingerprintAsync(StoredMeta meta, string title, string fingerprint, string? deletedAt)
    {
        await _js.InvokeVoidAsync("idbPutMeta", new
        {
            key = meta.key,
            id = meta.id,
            @namespace = meta.@namespace,
            title,
            lastUpdated = meta.lastUpdated,
            syncEnabled = meta.syncEnabled,
            contentFingerprint = fingerprint,
            deletedAt = deletedAt ?? "",
            titleIsCustom = meta.titleIsCustom ?? false
        });
    }

    private async Task<DateTime> TombstoneDeleteConversationAsync(string id, DateTime? deletedAtUtc = null)
    {
        var deletedAt = deletedAtUtc ?? DateTime.UtcNow;
        var deletedAtIso = deletedAt.ToString("o");
        var ns = GetPrefix();
        var metaKey = ns + ConvoPrefix + id;
        var existing = await GetMetaByIdAsync(id);
        var title = existing?.title ?? "(deleted)";
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);

        await _js.InvokeVoidAsync("idbPutMeta", new
        {
            key = existing?.key ?? metaKey,
            id,
            @namespace = ns,
            title,
            lastUpdated = deletedAtIso,
            syncEnabled,
            contentFingerprint = DeleteSyncPayload.AckValue(deletedAt.Ticks),
            deletedAt = deletedAtIso,
            titleIsCustom = existing?.titleIsCustom ?? false
        });

        await _js.InvokeVoidAsync("idbDeleteConvoContent", existing?.key ?? metaKey);

        try
        {
            var allMetas = await _js.InvokeAsync<List<StoredMeta>>("idbGetAllMetas");
            foreach (var m in allMetas.Where(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrEmpty(m.key))
                    await _js.InvokeVoidAsync("idbDeleteConvoContent", m.key);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmConvStore] Legacy content cleanup for {id}: {ex.Message}");
        }

        return deletedAt;
    }

    public async Task<bool> ShouldAcceptIncomingContentAsync(string id, List<ChatMessage> messages, CancellationToken ct = default)
    {
        var meta = await GetMetaByIdAsync(id);
        if (meta == null || string.IsNullOrEmpty(meta.deletedAt))
            return true;

        var deletedAtTicks = DateTime.Parse(meta.deletedAt).Ticks;
        return ChatMessageHelper.GetLatestContentTicks(messages) > deletedAtTicks;
    }

    public async Task<bool> TryApplyRemoteDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default)
    {
        var remoteDeletedAt = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        var meta = await GetMetaByIdAsync(id);
        if (meta == null)
            return false;

        if (!string.IsNullOrEmpty(meta.deletedAt))
        {
            var localDeletedAt = DateTime.Parse(meta.deletedAt);
            if (localDeletedAt.Ticks >= deletedAtTicks)
                return false;
        }
        else
        {
            var contentTicks = ChatMessageHelper.GetLatestContentTicks(await LoadConversationAsync(id, ct));
            if (contentTicks > deletedAtTicks || DateTime.Parse(meta.lastUpdated).Ticks > deletedAtTicks)
                return false;
        }

        await TombstoneDeleteConversationAsync(id, remoteDeletedAt);
        return true;
    }

    public async Task<string?> GetLastConvoIdAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var settingKey = ns + LastConvoKey;
        return await _js.InvokeAsync<string>("idbGetSetting", settingKey);
    }

    public async Task SetLastConvoIdAsync(string id, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var settingKey = ns + LastConvoKey;
        await _js.InvokeVoidAsync("idbPutSetting", settingKey, id);
    }

    public async Task<List<ChatMessage>> LoadConversationAsync(string id, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var fullKey = ns + ConvoPrefix + id;
        var json = await ReadContentAsync(fullKey);
        if (string.IsNullOrEmpty(json))
        {
            try
            {
                var allMetas = await _js.InvokeAsync<List<StoredMeta>>("idbGetAllMetas");
                var meta = allMetas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
                if (meta != null && !string.IsNullOrEmpty(meta.key) && meta.key != fullKey)
                    json = await ReadContentAsync(meta.key);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WasmConvStore] Legacy content key lookup failed for {id}: {ex.Message}");
            }
        }

        if (string.IsNullOrEmpty(json))
            return new List<ChatMessage>();

        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new();
        return ChatMessageHelper.NormalizeAll(messages);
    }

    public async Task SaveConversationAsync(string id, List<ChatMessage> messages, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var fullKey = ns + ConvoPrefix + id;
        var json = JsonSerializer.Serialize(messages);
        await WriteContentAsync(fullKey, json);
    }

    public Task<DateTime> DeleteConversationAsync(string id, CancellationToken ct = default) =>
        TombstoneDeleteConversationAsync(id);

    public async Task<string?> GetMetaTitleAsync(string id, CancellationToken ct = default)
    {
        var meta = await GetMetaByIdAsync(id);
        return meta?.title;
    }

    public async Task<(string Title, bool TitleIsCustom)> GetMetaTitleInfoAsync(string id, CancellationToken ct = default)
    {
        var meta = await GetMetaByIdAsync(id);
        var title = string.IsNullOrWhiteSpace(meta?.title) ? "(empty)" : meta.title;
        return (title, HasCustomTitle(meta));
    }

    public async Task SetConversationTitleAsync(string id, string title, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metaKey = ns + ConvoPrefix + id;
        var existing = await GetMetaByIdAsync(id);
        var normalizedTitle = NormalizeCustomTitle(title);
        var now = DateTime.UtcNow.ToString("o");
        bool syncEnabled = existing?.syncEnabled
            ?? (_auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email));
        var messages = await LoadConversationAsync(id, ct);
        var contentFingerprint = SyncFingerprint.ForConversation(id, normalizedTitle, messages);

        await _js.InvokeVoidAsync("idbPutMeta", new
        {
            key = existing?.key ?? metaKey,
            id,
            @namespace = ns,
            title = normalizedTitle,
            lastUpdated = now,
            syncEnabled,
            contentFingerprint,
            deletedAt = existing?.deletedAt ?? "",
            titleIsCustom = true
        });
    }

    public async Task UpdateIndexAfterSaveAsync(string id, List<ChatMessage> messages, List<LocalConvo> currentIndex, CancellationToken ct = default)
    {
        var existing = await GetMetaByIdAsync(id);

        string title;
        bool titleIsCustom;
        if (HasCustomTitle(existing))
        {
            title = string.IsNullOrWhiteSpace(existing!.title) ? "(empty)" : existing.title;
            titleIsCustom = true;
        }
        else
        {
            var raw = messages.FirstOrDefault(m => ChatMessageHelper.IsVisible(m) && (m.Role == "user" || m.User == "LocalUser"))?.Content
                      ?? messages.FirstOrDefault(m => ChatMessageHelper.IsVisible(m))?.Content;
            title = string.IsNullOrWhiteSpace(raw) ? "(empty)" : StripHtmlForTitle(raw);
            titleIsCustom = false;
        }

        var ns = GetPrefix();
        var metaKey = ns + ConvoPrefix + id;
        var now = DateTime.UtcNow.ToString("o");
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);
        var contentFingerprint = SyncFingerprint.ForConversation(id, title, messages);

        await _js.InvokeVoidAsync("idbPutMeta", new
        {
            key = existing?.key ?? metaKey,
            id,
            @namespace = ns,
            title,
            lastUpdated = now,
            syncEnabled,
            contentFingerprint,
            deletedAt = existing?.deletedAt ?? "",
            titleIsCustom
        });

        await SetLastConvoIdAsync(id, ct);
    }

    public async Task SetConversationSyncEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metaKey = ns + ConvoPrefix + id;

        var existing = (await _js.InvokeAsync<List<StoredMeta>>("idbGetMetasByNamespace", ns))
                       .FirstOrDefault(m => m.id == id);

        var now = existing?.lastUpdated ?? DateTime.UtcNow.ToString("o");

        await _js.InvokeVoidAsync("idbPutMeta", new
        {
            key = metaKey,
            id,
            @namespace = ns,
            title = existing?.title ?? "(empty)",
            lastUpdated = now,
            syncEnabled = enabled,
            contentFingerprint = existing?.contentFingerprint ?? "",
            deletedAt = existing?.deletedAt ?? "",
            titleIsCustom = existing?.titleIsCustom ?? false
        });
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