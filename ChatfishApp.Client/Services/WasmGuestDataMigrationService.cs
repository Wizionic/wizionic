using System.Text.Json;
using Microsoft.JSInterop;
using static ChatfishApp.Client.Services.WasmConversationStore;

namespace ChatfishApp.Client.Services;

/// <summary>
/// When a guest user logs in, their chat history and notes live under the guest
/// namespace ("wasmchat-") encrypted with a device-local key. Authenticated data uses
/// a per-user namespace ("u-{userId}-") and the server-provided encryption key.
/// This service migrates guest data into the authenticated namespace on login,
/// re-encrypting content with the server key so sync and cross-device access work.
/// </summary>
public class WasmGuestDataMigrationService
{
    private const string GuestNamespace = "wasmchat-";
    private const string ConvoPrefix = "wasmchat-convo-";
    private const string NotePrefix = "n-wasmchat-note-";
    private const string LastConvoKey = "wasmchat-last-convo";
    private const string GuestEncryptionKeySetting = "guest-encryption-key";

    private readonly IJSRuntime _js;
    private readonly WasmAuthService _auth;
    private readonly WasmCryptoService _crypto;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? OnMigrated;

    public WasmGuestDataMigrationService(IJSRuntime js, WasmAuthService auth, WasmCryptoService crypto)
    {
        _js = js;
        _auth = auth;
        _crypto = crypto;
        _auth.OnChanged += OnAuthChanged;
    }

    private async void OnAuthChanged()
    {
        if (!_auth.IsAuthenticated)
            return;

        try
        {
            await MigrateIfNeededAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GuestMigration] OnAuthChanged migration failed: {ex.Message}");
        }
    }

    public async Task MigrateIfNeededAsync()
    {
        if (!_auth.IsAuthenticated
            || string.IsNullOrEmpty(_auth.UserId)
            || string.IsNullOrEmpty(_auth.LocalEncryptionKeyB64))
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            var authNs = GetAuthNamespace();
            var authKey = _auth.LocalEncryptionKeyB64;

            var guestConvoMetas = await GetGuestConvoMetasAsync();
            var guestNoteMetas = await GetGuestNoteMetasAsync();
            if (guestConvoMetas.Count == 0 && guestNoteMetas.Count == 0)
                return;

            var guestKey = await _js.InvokeAsync<string?>("idbGetSetting", GuestEncryptionKeySetting);
            if (string.IsNullOrEmpty(guestKey))
            {
                Console.WriteLine("[GuestMigration] Guest data found but no guest encryption key; skipping migration.");
                return;
            }

            var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var migratedConvos = await MigrateConversationsAsync(guestConvoMetas, authNs, authKey, guestKey, idMap);
            var migratedNotes = await MigrateNotesAsync(guestNoteMetas, authNs, authKey, guestKey, idMap);
            await MigrateLastConvoSettingAsync(authNs, idMap);

            if (migratedConvos > 0 || migratedNotes > 0)
            {
                Console.WriteLine($"[GuestMigration] Migrated {migratedConvos} conversation(s) and {migratedNotes} note(s) to {authNs}");
                OnMigrated?.Invoke();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetAuthNamespace() => $"u-{_auth.UserId}-";

    private async Task<List<ConvoStoredMeta>> GetGuestConvoMetasAsync()
    {
        var metas = await _js.InvokeAsync<List<ConvoStoredMeta>>("idbGetMetasByNamespace", GuestNamespace);
        return metas
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .ToList();
    }

    private async Task<List<NoteStoredMeta>> GetGuestNoteMetasAsync()
    {
        var metas = await _js.InvokeAsync<List<NoteStoredMeta>>("idbGetNoteMetasByNamespace", GuestNamespace);
        return metas
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .ToList();
    }

    private async Task<int> MigrateConversationsAsync(
        List<ConvoStoredMeta> guestMetas,
        string authNs,
        string authKey,
        string guestKey,
        Dictionary<string, string> idMap)
    {
        if (guestMetas.Count == 0)
            return 0;

        var authMetas = await _js.InvokeAsync<List<ConvoStoredMeta>>("idbGetMetasByNamespace", authNs);
        var authIds = authMetas
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .Select(m => m.id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var migrated = 0;
        foreach (var guestMeta in guestMetas)
        {
            var targetId = ResolveTargetId(guestMeta.id, authIds, idMap);
            if (!await TryReencryptAndStoreConvoAsync(guestMeta, targetId, authNs, authKey, guestKey))
                continue;

            authIds.Add(targetId);
            migrated++;
        }

        return migrated;
    }

    private async Task<int> MigrateNotesAsync(
        List<NoteStoredMeta> guestMetas,
        string authNs,
        string authKey,
        string guestKey,
        Dictionary<string, string> idMap)
    {
        if (guestMetas.Count == 0)
            return 0;

        var authMetas = await _js.InvokeAsync<List<NoteStoredMeta>>("idbGetNoteMetasByNamespace", authNs);
        var authIds = authMetas
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .Select(m => m.id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var migrated = 0;
        foreach (var guestMeta in guestMetas)
        {
            var targetId = ResolveTargetId(guestMeta.id, authIds, idMap);
            if (!await TryReencryptAndStoreNoteAsync(guestMeta, targetId, authNs, authKey, guestKey))
                continue;

            authIds.Add(targetId);
            migrated++;
        }

        return migrated;
    }

    private static string ResolveTargetId(string guestId, HashSet<string> authIds, Dictionary<string, string> idMap)
    {
        if (!authIds.Contains(guestId))
        {
            idMap[guestId] = guestId;
            return guestId;
        }

        var newId = Guid.NewGuid().ToString("N");
        idMap[guestId] = newId;
        return newId;
    }

    private async Task<bool> TryReencryptAndStoreConvoAsync(
        ConvoStoredMeta guestMeta,
        string targetId,
        string authNs,
        string authKey,
        string guestKey)
    {
        var encrypted = await _js.InvokeAsync<string?>("idbGetContent", guestMeta.key);
        if (string.IsNullOrEmpty(encrypted))
        {
            await _js.InvokeVoidAsync("idbDeleteByKey", guestMeta.key);
            return false;
        }

        var plaintext = await _crypto.DecryptAsync(guestKey, encrypted);
        if (string.IsNullOrEmpty(plaintext))
        {
            Console.WriteLine($"[GuestMigration] Could not decrypt conversation {guestMeta.id}; leaving guest copy.");
            return false;
        }

        var reencrypted = await _crypto.EncryptAsync(authKey, plaintext);
        var newKey = authNs + ConvoPrefix + targetId;
        await _js.InvokeVoidAsync("idbPutContent", newKey, reencrypted);

        var title = string.IsNullOrWhiteSpace(guestMeta.title) ? "(empty)" : guestMeta.title;
        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(plaintext) ?? new List<ChatMessage>();
        messages = ChatMessageHelper.NormalizeAll(messages);
        var fingerprint = SyncFingerprint.ForConversation(targetId, title, messages);

        await _js.InvokeVoidAsync("idbPutMeta", new
        {
            key = newKey,
            id = targetId,
            @namespace = authNs,
            title,
            lastUpdated = guestMeta.lastUpdated,
            syncEnabled = true,
            contentFingerprint = fingerprint,
            deletedAt = "",
            titleIsCustom = guestMeta.titleIsCustom ?? false
        });

        await _js.InvokeVoidAsync("idbDeleteByKey", guestMeta.key);
        return true;
    }

    private async Task<bool> TryReencryptAndStoreNoteAsync(
        NoteStoredMeta guestMeta,
        string targetId,
        string authNs,
        string authKey,
        string guestKey)
    {
        var encrypted = await _js.InvokeAsync<string?>("idbGetNoteContent", guestMeta.key);
        if (string.IsNullOrEmpty(encrypted))
        {
            await _js.InvokeVoidAsync("idbDeleteNoteByKey", guestMeta.key);
            return false;
        }

        var plaintext = await _crypto.DecryptAsync(guestKey, encrypted);
        if (string.IsNullOrEmpty(plaintext))
        {
            Console.WriteLine($"[GuestMigration] Could not decrypt note {guestMeta.id}; leaving guest copy.");
            return false;
        }

        var reencrypted = await _crypto.EncryptAsync(authKey, plaintext);
        var newKey = authNs + NotePrefix + targetId;
        await _js.InvokeVoidAsync("idbPutNoteContent", newKey, reencrypted);

        var title = string.IsNullOrWhiteSpace(guestMeta.title) ? "(empty)" : guestMeta.title;
        var entries = JsonSerializer.Deserialize<List<ChatMessage>>(plaintext) ?? new List<ChatMessage>();
        entries = ChatMessageHelper.NormalizeAll(entries);
        var fingerprint = SyncFingerprint.ForNote(targetId, title, entries);

        await _js.InvokeVoidAsync("idbPutNoteMeta", new
        {
            key = newKey,
            id = targetId,
            @namespace = authNs,
            title,
            lastUpdated = guestMeta.lastUpdated,
            syncEnabled = true,
            contentFingerprint = fingerprint,
            deletedAt = ""
        });

        await _js.InvokeVoidAsync("idbDeleteNoteByKey", guestMeta.key);
        return true;
    }

    private async Task MigrateLastConvoSettingAsync(string authNs, Dictionary<string, string> idMap)
    {
        var guestLastConvo = await _js.InvokeAsync<string?>("idbGetSetting", GuestNamespace + LastConvoKey);
        if (string.IsNullOrEmpty(guestLastConvo))
            return;

        var authSettingKey = authNs + LastConvoKey;
        var authLastConvo = await _js.InvokeAsync<string?>("idbGetSetting", authSettingKey);
        if (!string.IsNullOrEmpty(authLastConvo))
            return;

        var mappedId = idMap.TryGetValue(guestLastConvo, out var mapped) ? mapped : guestLastConvo;
        await _js.InvokeVoidAsync("idbPutSetting", authSettingKey, mappedId);
    }

    private record ConvoStoredMeta(
        string key,
        string id,
        string @namespace,
        string title,
        string lastUpdated,
        bool syncEnabled,
        string? contentFingerprint,
        string? deletedAt,
        bool? titleIsCustom);

    private record NoteStoredMeta(
        string key,
        string id,
        string @namespace,
        string title,
        string lastUpdated,
        bool syncEnabled,
        string? contentFingerprint,
        string? deletedAt);
}