using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatfishApp.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ChatfishApp.Core.Auth;

/// <summary>
/// Unified auth for WASM (browser cookies) and MAUI (CookieContainer HttpClient).
/// </summary>
public class ChatAuthService : IAuthService
{
    private readonly HttpClient _http;
    private readonly IGuestKeyProvider? _guestKeys;
    private readonly ChatfishServerOptions? _serverOptions;
    private readonly IAuthSessionPersistence? _sessionPersistence;

    public string? Email { get; private set; }
    public string? UserId { get; private set; }
    public string? LocalEncryptionKeyB64 { get; private set; }
    public bool HasPassword { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(Email) && !string.IsNullOrEmpty(LocalEncryptionKeyB64);

    public string ServerBaseUrl =>
        _serverOptions?.BaseUrl.TrimEnd('/')
        ?? _http.BaseAddress?.ToString().TrimEnd('/')
        ?? "";

    public string SyncHubUrl =>
        _serverOptions?.SyncHubUrl
        ?? (_http.BaseAddress is not null
            ? new Uri(_http.BaseAddress, "sync-hub").ToString()
            : "/sync-hub");

    public event Action? OnChanged;

    private string? _guestKeyB64;

    public ChatAuthService(
        HttpClient http,
        IGuestKeyProvider? guestKeys = null,
        IOptions<ChatfishServerOptions>? serverOptions = null,
        IAuthSessionPersistence? sessionPersistence = null)
    {
        _http = http;
        _guestKeys = guestKeys;
        _serverOptions = serverOptions?.Value;
        _sessionPersistence = sessionPersistence;
    }

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
                if (_sessionPersistence is not null)
                    await _sessionPersistence.PersistCookiesAsync();
                break;
            case AuthFetchResult.Unauthorized:
                ClearAuthState();
                if (_sessionPersistence is not null)
                    await _sessionPersistence.ClearCookiesAsync();
                break;
            case AuthFetchResult.TransientError:
                Console.WriteLine("[Auth] Auth check failed after retry (server may be waking); keeping prior auth state if any.");
                break;
            case AuthFetchResult.Incomplete:
                ClearAuthState();
                break;
        }

        OnChanged?.Invoke();
    }

    public async Task<(bool Success, string? Error)> RequestLoginCodeAsync(string email)
    {
        try
        {
            var payload = new { Email = email.Trim() };
            var resp = await _http.PostAsJsonAsync("api/auth/request-magic-link", payload);

            if (resp.IsSuccessStatusCode)
                return (true, null);

            var txt = await resp.Content.ReadAsStringAsync();
            return (false, $"Could not send login code ({(int)resp.StatusCode}): {txt}");
        }
        catch (Exception ex)
        {
            return (false, "Network error: " + ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> VerifyLoginCodeAsync(string email, string code)
    {
        try
        {
            var payload = new { Email = email.Trim(), Code = code.Trim() };
            var resp = await _http.PostAsJsonAsync("api/auth/verify-code", payload);

            if (!resp.IsSuccessStatusCode)
            {
                return resp.StatusCode == HttpStatusCode.Unauthorized
                    ? (false, "Invalid or expired login code. Request a new one.")
                    : (false, $"Could not verify code ({(int)resp.StatusCode}).");
            }

            await LoadAsync();
            return IsAuthenticated
                ? (true, null)
                : (false, "Signed in on the server, but the session could not be loaded.");
        }
        catch (Exception ex)
        {
            return (false, "Network error: " + ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> LoginWithPasswordAsync(string email, string password)
    {
        try
        {
            var payload = new { Email = email.Trim(), Password = password };
            var resp = await _http.PostAsJsonAsync("api/auth/login-password", payload);

            if (!resp.IsSuccessStatusCode)
            {
                // Server intentionally returns a generic message; surface that (or a safe default).
                string message = "Invalid email or password.";
                try
                {
                    var body = await ReadJsonOrNullAsync<ErrorMessageResponse>(resp);
                    if (!string.IsNullOrWhiteSpace(body?.Message))
                        message = body.Message;
                }
                catch { /* keep default */ }

                return (false, message);
            }

            await LoadAsync();
            return IsAuthenticated
                ? (true, null)
                : (false, "Signed in on the server, but the session could not be loaded.");
        }
        catch (Exception ex)
        {
            return (false, "Network error: " + ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> SetPasswordAsync(string password, string confirmPassword, string? currentPassword = null)
    {
        try
        {
            var payload = new
            {
                Password = password,
                ConfirmPassword = confirmPassword,
                CurrentPassword = currentPassword
            };
            var resp = await _http.PostAsJsonAsync("api/auth/set-password", payload);

            if (!resp.IsSuccessStatusCode)
            {
                string message = $"Could not save password ({(int)resp.StatusCode}).";
                try
                {
                    var body = await ReadJsonOrNullAsync<ErrorMessageResponse>(resp);
                    if (!string.IsNullOrWhiteSpace(body?.Message))
                        message = body.Message;
                }
                catch { /* keep default */ }

                return (false, message);
            }

            HasPassword = true;
            OnChanged?.Invoke();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "Network error: " + ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> VerifyPasswordAsync(string password)
    {
        try
        {
            var payload = new { Password = password };
            var resp = await _http.PostAsJsonAsync("api/auth/verify-password", payload);

            if (resp.IsSuccessStatusCode)
                return (true, null);

            string message = "Incorrect password.";
            try
            {
                var body = await ReadJsonOrNullAsync<ErrorMessageResponse>(resp);
                if (!string.IsNullOrWhiteSpace(body?.Message))
                    message = body.Message;
            }
            catch { /* keep default */ }

            return (false, message);
        }
        catch (Exception ex)
        {
            return (false, "Network error: " + ex.Message);
        }
    }

    public async Task SignOutAsync()
    {
        try
        {
            await _http.PostAsync("api/auth/logout", null);
        }
        catch
        {
            // Best effort.
        }

        ClearAuthState();
        if (_sessionPersistence is not null)
            await _sessionPersistence.ClearCookiesAsync();
        OnChanged?.Invoke();
    }

    public void SignOutLocal()
    {
        ClearAuthState();
        if (_sessionPersistence is not null)
            _ = _sessionPersistence.ClearCookiesAsync();
        OnChanged?.Invoke();
    }

    public async Task<string> GetOrCreateHistoryEncryptionKeyAsync()
    {
        if (!string.IsNullOrEmpty(LocalEncryptionKeyB64))
            return LocalEncryptionKeyB64;

        if (!string.IsNullOrEmpty(_guestKeyB64))
            return _guestKeyB64;

        if (_guestKeys is null)
            return string.Empty;

        try
        {
            _guestKeyB64 = await _guestKeys.GetOrCreateGuestKeyAsync();
            return _guestKeyB64;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] Could not ensure guest encryption key: {ex.Message}");
            return string.Empty;
        }
    }

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
            using var meResponse = await _http.GetAsync("api/auth/me");
            if (meResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return AuthFetchResult.Unauthorized;

            if (!meResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Auth] /api/auth/me returned {(int)meResponse.StatusCode}");
                return AuthFetchResult.TransientError;
            }

            var me = await ReadJsonOrNullAsync<UserMeResponse>(meResponse);
            if (me == null || string.IsNullOrEmpty(me.Email))
                return AuthFetchResult.Unauthorized;

            using var keyResponse = await _http.GetAsync("api/user/encryption-key");
            if (keyResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return AuthFetchResult.Unauthorized;

            if (!keyResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Auth] /api/user/encryption-key returned {(int)keyResponse.StatusCode}");
                return AuthFetchResult.TransientError;
            }

            var keyResp = await ReadJsonOrNullAsync<EncryptionKeyResponse>(keyResponse);
            if (string.IsNullOrEmpty(keyResp?.Key))
                return AuthFetchResult.Incomplete;

            Email = me.Email;
            UserId = me.Id;
            LocalEncryptionKeyB64 = keyResp.Key;
            HasPassword = me.HasPassword;
            return AuthFetchResult.Success;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[Auth] Network error during auth check: {ex.Message}");
            return AuthFetchResult.TransientError;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("[Auth] Auth check timed out.");
            return AuthFetchResult.TransientError;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[Auth] Non-JSON auth response (likely login redirect HTML): {ex.Message}");
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
        HasPassword = false;
    }

    private record UserMeResponse(string? Email, string? Id, bool HasLocalEncryptionKey, bool HasPassword = false);
    private record EncryptionKeyResponse(string? Key);
    private record ErrorMessageResponse(string? Message);
}