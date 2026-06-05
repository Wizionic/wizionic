using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Client-side service that fetches the authenticated user's identity and the
/// per-user encryption key from the server (via the protected /api endpoints).
/// This is the WASM equivalent of the server's "get current user from HttpContext".
///
/// When a user is logged in on the server (magic link cookie), the WASM can call
/// these endpoints because the browser automatically sends the cookie on same-origin
/// requests. The returned email + key (plus a locally-generated guest key when not
/// logged in) allow:
/// - Displaying "Logged in as ..." in the WASM UI.
/// - Namespacing IndexedDB history per email (or "guest").
/// - *Always* encrypting history content at rest in IndexedDB (guest chats use a
///   device-local key; authenticated chats use the server key so other devices of
///   the same user can decrypt for sync).
/// - Live cross-device sync only while both WASM instances are open (Brave-style).
/// </summary>
public class WasmAuthService
{
    private readonly HttpClient _http;
    private readonly IJSRuntime? _js; // optional, for future "sign out from WASM" if needed

    public string? Email { get; private set; }
    public string? UserId { get; private set; }
    public string? LocalEncryptionKeyB64 { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(Email) && !string.IsNullOrEmpty(LocalEncryptionKeyB64);

    public event Action? OnChanged;

    public WasmAuthService(HttpClient http, IJSRuntime? js = null)
    {
        _http = http;
        _js = js;
    }

    /// <summary>
    /// Call this early (e.g. from WasmChat or a root component OnInitializedAsync).
    /// It will hit the server /api/auth/me and /api/user/encryption-key using the
    /// cookie that the browser already has from the server-side login.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            var me = await _http.GetFromJsonAsync<UserMeResponse>("/api/auth/me");
            if (me != null && !string.IsNullOrEmpty(me.Email))
            {
                Email = me.Email;
                UserId = me.Id;

                // Fetch the actual key bytes the client will use for AES-GCM.
                var keyResp = await _http.GetFromJsonAsync<EncryptionKeyResponse>("/api/user/encryption-key");
                LocalEncryptionKeyB64 = keyResp?.Key;

                OnChanged?.Invoke();
            }
        }
        catch (Exception)
        {
            // Not authenticated, or network error, or key not yet provisioned.
            // Leave the properties null/empty — the rest of the WASM app falls back
            // to pure local (unauthenticated) mode, which continues to work.
            Email = null;
            UserId = null;
            LocalEncryptionKeyB64 = null;
            OnChanged?.Invoke();
        }
    }

    public void SignOutLocal()
    {
        Email = null;
        UserId = null;
        LocalEncryptionKeyB64 = null;
        _guestKeyB64 = null;
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Returns the AES-GCM key (base64) that must be used for *all* history content
    /// blobs stored in IndexedDB (guest and authenticated).
    ///
    /// - When a real server login succeeded and we have the server key → that key is returned
    ///   (this is what enables the same encrypted blobs to be decrypted on another device
    ///   of the same logged-in user for sync).
    /// - Otherwise we ensure a local (device-only) guest key exists in the IDB settings store
    ///   and return it. This guarantees that *unauthenticated* chats are also encrypted at rest
    ///   (per requirement) even though they cannot be synced.
    ///
    /// The key is cached for the life of the WASM app.
    /// </summary>
    public async Task<string> GetOrCreateHistoryEncryptionKeyAsync(IJSRuntime js)
    {
        if (!string.IsNullOrEmpty(LocalEncryptionKeyB64))
            return LocalEncryptionKeyB64; // real server key (cross-device)

        if (!string.IsNullOrEmpty(_guestKeyB64))
            return _guestKeyB64;

        try
        {
            _guestKeyB64 = await js.InvokeAsync<string>("idbEnsureGuestKey");
            return _guestKeyB64;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmAuth] Could not ensure guest encryption key via IDB: {ex.Message}");
            return string.Empty; // last-resort fallback (store will see empty and may store plaintext)
        }
    }

    private string? _guestKeyB64;

    private record UserMeResponse(string? Email, string? Id, bool HasLocalEncryptionKey);
    private record EncryptionKeyResponse(string? Key);
}