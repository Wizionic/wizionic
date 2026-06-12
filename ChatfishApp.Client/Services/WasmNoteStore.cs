using Microsoft.JSInterop;
using System.Text;
using System.Text.Json;
using static ChatfishApp.Client.Services.WasmConversationStore;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Client-side storage for notes (parallel to WasmConversationStore but for user notes).
/// Uses noteMetas + noteContents IDB object stores with AES-256-GCM encryption.
/// Each note contains a list of messages stored identically to conversation ChatMessage records.
/// </summary>
public class WasmNoteStore
{
    private const string NotePrefix = "n-wasmchat-note-";

    private readonly WasmAuthService _auth;
    private readonly WasmCryptoService _crypto;

    public WasmNoteStore(WasmAuthService auth, WasmCryptoService crypto)
    {
        _auth = auth;
        _crypto = crypto;
    }

    public record LocalNote(string Id, string Title, DateTime LastUpdated);

    private record StoredMeta(string key, string id, string @namespace, string title, string lastUpdated, bool syncEnabled);

    private static string GetStableHash(string input) => 
        string.IsNullOrEmpty(input) ? "00000000" : BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(input)), 0, 4).Replace("-", "").ToLowerInvariant();

    private string GetPrefix()
    {
        if (_auth.IsAuthenticated)
        {
            if (!string.IsNullOrEmpty(_auth.UserId)) return $"u-{_auth.UserId}-";
            if (!string.IsNullOrEmpty(_auth.Email)) return $"e-{GetStableHash(_auth.Email)}-";
        }
        return "wasmchat-";
    }

    private async Task<string> GetNoteKeyAsync(IJSRuntime js) => await _auth.GetOrCreateHistoryEncryptionKeyAsync(js);

    public async Task<List<LocalNote>> LoadIndexAsync(IJSRuntime js)
    {
        var ns = GetPrefix();
        var metas = await js.InvokeAsync<List<StoredMeta>>("idbGetNoteMetasByNamespace", ns);
        return metas.OrderByDescending(m => m.lastUpdated)
                    .Select(m => new LocalNote(m.id, string.IsNullOrWhiteSpace(m.title) ? "(empty)" : m.title, DateTime.Parse(m.lastUpdated)))
                    .ToList();
    }

    public async Task<List<ChatMessage>> LoadNoteAsync(IJSRuntime js, string id)
    {
        var ns = GetPrefix();
        var fullKey = ns + NotePrefix + id;
        var encrypted = await js.InvokeAsync<string>("idbGetNoteContent", fullKey);
        if (string.IsNullOrEmpty(encrypted)) return new List<ChatMessage>();

        var keyB64 = await GetNoteKeyAsync(js);
        string json = encrypted;
        if (!string.IsNullOrEmpty(keyB64))
            json = await _crypto.DecryptAsync(keyB64, encrypted);

        if (string.IsNullOrEmpty(json)) return new List<ChatMessage>();

        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json);
        return messages ?? new List<ChatMessage>();
    }

    public async Task SaveNoteAsync(IJSRuntime js, string id, List<ChatMessage> entries)
    {
        var ns = GetPrefix();
        var fullKey = ns + NotePrefix + id;
        var json = JsonSerializer.Serialize(entries);
        var keyB64 = await GetNoteKeyAsync(js);
        string toStore = json;
        if (!string.IsNullOrEmpty(keyB64))
            toStore = await _crypto.EncryptAsync(keyB64, json);
        await js.InvokeVoidAsync("idbPutNoteContent", fullKey, toStore);
    }

    public async Task UpdateIndexAfterSaveAsync(IJSRuntime js, string id, string title)
    {
        var ns = GetPrefix();
        var metaKey = ns + NotePrefix + id;
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);
        await js.InvokeVoidAsync("idbPutNoteMeta", new {
            key = metaKey, id, @namespace = ns,
            title = string.IsNullOrWhiteSpace(title) ? "(empty)" : title,
            lastUpdated = DateTime.UtcNow.ToString("o"), syncEnabled });
    }

    public async Task DeleteNoteAsync(IJSRuntime js, string id)
    {
        var ns = GetPrefix();
        await js.InvokeVoidAsync("idbDeleteNoteByKey", ns + NotePrefix + id);
    }
}
