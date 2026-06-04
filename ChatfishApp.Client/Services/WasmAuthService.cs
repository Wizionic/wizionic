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
/// requests. The returned email + key allow:
/// - Displaying "Logged in as ..." in the WASM UI.
/// - Namespacing localStorage / IndexedDB per email.
/// - Encrypting local history blobs and live-sync payloads (so the server relay
///   never sees plaintext chat content, and history is never written to the
///   central SQLite DB for the WASM path).
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
        OnChanged?.Invoke();
    }

    private record UserMeResponse(string? Email, string? Id, bool HasLocalEncryptionKey);
    private record EncryptionKeyResponse(string? Key);
}