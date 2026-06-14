using System.Net;
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
    /// Call this early (e.g. from Chat or a root component OnInitializedAsync).
    /// It will hit the server /api/auth/me and /api/user/encryption-key using the
    /// cookie that the browser already has from the server-side login.
    /// </summary>
    public async Task LoadAsync()
    {
        var result = await TryFetchAuthStateAsync();
        if (result == AuthFetchResult.Success)
        {
            OnChanged?.Invoke();
            return;
        }

        if (result == AuthFetchResult.TransientError)
        {
            await Task.Delay(750);
            result = await TryFetchAuthStateAsync();
        }

        switch (result)
        {
            case AuthFetchResult.Success:
                break;
            case AuthFetchResult.Unauthorized:
                ClearAuthState();
                break;
            case AuthFetchResult.TransientError:
                Console.WriteLine("[WasmAuth] Auth check failed after retry (server may be waking); keeping prior auth state if any.");
                break;
            case AuthFetchResult.Incomplete:
                ClearAuthState();
                break;
        }

        OnChanged?.Invoke();
    }

    public void SignOutLocal()
    {
        ClearAuthState();
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

    private enum AuthFetchResult
    {
        Success,
        Unauthorized,
        TransientError,
        Incomplete
    }

    private async Task<AuthFetchResult> TryFetchAuthStateAsync()
    {
        try
        {
            using var meResponse = await _http.GetAsync("/api/auth/me");
            if (meResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return AuthFetchResult.Unauthorized;

            if (!meResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[WasmAuth] /api/auth/me returned {(int)meResponse.StatusCode}");
                return AuthFetchResult.TransientError;
            }

            var me = await ReadJsonOrNullAsync<UserMeResponse>(meResponse);
            if (me == null || string.IsNullOrEmpty(me.Email))
                return AuthFetchResult.Unauthorized;

            using var keyResponse = await _http.GetAsync("/api/user/encryption-key");
            if (keyResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return AuthFetchResult.Unauthorized;

            if (!keyResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[WasmAuth] /api/user/encryption-key returned {(int)keyResponse.StatusCode}");
                return AuthFetchResult.TransientError;
            }

            var keyResp = await ReadJsonOrNullAsync<EncryptionKeyResponse>(keyResponse);
            if (string.IsNullOrEmpty(keyResp?.Key))
                return AuthFetchResult.Incomplete;

            Email = me.Email;
            UserId = me.Id;
            LocalEncryptionKeyB64 = keyResp.Key;
            return AuthFetchResult.Success;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[WasmAuth] Network error during auth check: {ex.Message}");
            return AuthFetchResult.TransientError;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("[WasmAuth] Auth check timed out.");
            return AuthFetchResult.TransientError;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[WasmAuth] Non-JSON auth response (likely login redirect HTML): {ex.Message}");
            return AuthFetchResult.Unauthorized;
        }
    }

    private static async Task<T?> ReadJsonOrNullAsync<T>(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return default;

        return await response.Content.ReadFromJsonAsync<T>();
    }

    private void ClearAuthState()
    {
        Email = null;
        UserId = null;
        LocalEncryptionKeyB64 = null;
    }

    private record UserMeResponse(string? Email, string? Id, bool HasLocalEncryptionKey);
    private record EncryptionKeyResponse(string? Key);
}