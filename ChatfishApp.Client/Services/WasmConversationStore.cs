using Microsoft.JSInterop;
using System.Text.Json;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Client-side storage for conversation history (multi-convo, titles, messages).
/// Everything stays in browser localStorage for the WASM local-first target.
/// </summary>
public class WasmConversationStore
{
    private const string ConvosIndexKey = "wasmchat-convos";
    private const string LastConvoKey = "wasmchat-last-convo";
    private const string ConvoPrefix = "wasmchat-convo-";

    private readonly WasmAuthService _auth;
    private readonly WasmCryptoService _crypto;

    public WasmConversationStore(WasmAuthService auth, WasmCryptoService crypto)
    {
        _auth = auth;
        _crypto = crypto;

        // When the user logs in (or out) we may want to re-load under the new
        // per-email namespaced + (possibly) encrypted keys. The page can call
        // LoadIndexAsync again after auth changes if desired.
        _auth.OnChanged += () => { /* consumer decides when to reload */ };
    }

    public record LocalConvo(string Id, string Title, DateTime LastUpdated);

    private string GetPrefix()
    {
        return _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email)
            ? $"wasmchat-{_auth.Email.GetHashCode():x8}-"
            : "wasmchat-";
    }

    private async Task<string?> ReadValueAsync(IJSRuntime js, string fullKey)
    {
        var stored = await js.InvokeAsync<string>("localStorage.getItem", fullKey);
        if (string.IsNullOrEmpty(stored) || !_auth.IsAuthenticated || string.IsNullOrEmpty(_auth.LocalEncryptionKeyB64))
            return stored; // unauth or legacy plaintext

        return await _crypto.DecryptAsync(_auth.LocalEncryptionKeyB64, stored);
    }

    private async Task WriteValueAsync(IJSRuntime js, string fullKey, string json)
    {
        string toStore = json;
        if (_auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.LocalEncryptionKeyB64))
        {
            toStore = await _crypto.EncryptAsync(_auth.LocalEncryptionKeyB64, json);
        }
        await js.InvokeVoidAsync("localStorage.setItem", fullKey, toStore);
    }

    public async Task<List<LocalConvo>> LoadIndexAsync(IJSRuntime js)
    {
        var prefix = GetPrefix();
        var json = await ReadValueAsync(js, prefix + ConvosIndexKey);
        var list = string.IsNullOrEmpty(json)
            ? new List<LocalConvo>()
            : JsonSerializer.Deserialize<List<LocalConvo>>(json) ?? new();

        // Clean polluted titles (legacy from when we stored formatted HTML)
        bool changed = false;
        var cleaned = new List<LocalConvo>();
        foreach (var c in list)
        {
            var cleanTitle = (c.Title?.Contains('<') == true) ? StripHtmlForTitle(c.Title) : (c.Title ?? "(empty)");
            if (cleanTitle != c.Title) changed = true;
            cleaned.Add(new LocalConvo(c.Id, cleanTitle, c.LastUpdated));
        }

        if (changed)
        {
            await SaveIndexAsync(js, cleaned);
        }

        return cleaned;
    }

    public async Task SaveIndexAsync(IJSRuntime js, List<LocalConvo> convos)
    {
        var prefix = GetPrefix();
        var json = JsonSerializer.Serialize(convos);
        await WriteValueAsync(js, prefix + ConvosIndexKey, json);
    }

    public async Task<string?> GetLastConvoIdAsync(IJSRuntime js)
    {
        var prefix = GetPrefix();
        return await ReadValueAsync(js, prefix + LastConvoKey);
    }

    public async Task SetLastConvoIdAsync(IJSRuntime js, string id)
    {
        var prefix = GetPrefix();
        var json = JsonSerializer.Serialize(id);
        await WriteValueAsync(js, prefix + LastConvoKey, json);
    }

    public async Task<List<ChatMessage>> LoadConversationAsync(IJSRuntime js, string id)
    {
        var prefix = GetPrefix();
        var json = await ReadValueAsync(js, prefix + ConvoPrefix + id);
        if (string.IsNullOrEmpty(json)) return new List<ChatMessage>();
        return JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new();
    }

    public async Task SaveConversationAsync(IJSRuntime js, string id, List<ChatMessage> messages)
    {
        var prefix = GetPrefix();
        var json = JsonSerializer.Serialize(messages);
        await WriteValueAsync(js, prefix + ConvoPrefix + id, json);
    }

    public async Task DeleteConversationAsync(IJSRuntime js, string id)
    {
        var prefix = GetPrefix();
        await js.InvokeVoidAsync("localStorage.removeItem", prefix + ConvoPrefix + id);
    }

    public async Task UpdateIndexAfterSaveAsync(IJSRuntime js, string id, List<ChatMessage> messages, List<LocalConvo> currentIndex)
    {
        // During transition we still support the old "LocalUser" sentinel.
        // After the WasmChat page is updated to store Role="user", this will
        // naturally pick the first user message.
        var title = messages.FirstOrDefault(m => m.Role == "user" || m.User == "LocalUser")?.Content
                    ?? messages.FirstOrDefault()?.Content;

        title = string.IsNullOrWhiteSpace(title) ? "(empty)" : StripHtmlForTitle(title);
        if (title.Length > 30) title = title.Substring(0, 30) + "...";

        var existing = currentIndex.FirstOrDefault(c => c.Id == id);
        var updated = new LocalConvo(id, title, DateTime.UtcNow);

        var newIndex = currentIndex.Where(c => c.Id != id).ToList();
        newIndex.Add(updated);

        await SaveIndexAsync(js, newIndex);
        await SetLastConvoIdAsync(js, id);
    }

    private static string StripHtmlForTitle(string htmlOrText)
    {
        if (string.IsNullOrWhiteSpace(htmlOrText)) return "(empty)";
        var plain = System.Text.RegularExpressions.Regex.Replace(htmlOrText, "<.*?>", string.Empty);
        plain = System.Net.WebUtility.HtmlDecode(plain).Trim();
        if (plain.Length > 30) plain = plain.Substring(0, 30) + "...";
        return plain;
    }

    // ChatMessage shape used for WASM local (and live sync) storage.
    // We keep the old "User" field for backward compat with existing localStorage data.
    // New code (and the live sync path) should prefer Role + raw Content (matching the
    // server Message entity) so that cross-device sync and the main hosted chat can
    // eventually share the same logical format.
    public record ChatMessage(string? Role = null, string Content = "", string? ModelUsed = null, DateTime? Timestamp = null, string? User = null, string? ToolTrace = null);
}
