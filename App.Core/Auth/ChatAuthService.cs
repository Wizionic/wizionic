using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Configuration;
using Microsoft.Extensions.Options;
// IAppServerEndpoint lives in Configuration

namespace App.Core.Auth;

/// <summary>
/// Unified auth for WASM (browser cookies) and MAUI (CookieContainer HttpClient).
/// </summary>
public class ChatAuthService : IAuthService
{
    private readonly HttpClient _http;
    private readonly AppServerOptions? _serverOptions;
    private readonly IAppServerEndpoint? _endpoint;
    private readonly IAuthSessionPersistence? _sessionPersistence;
    private readonly IClientDeviceId? _deviceId;

    public string? Email { get; private set; }
    public string? UserId { get; private set; }
    public string? LocalEncryptionKeyB64 { get; private set; }
    public bool HasPassword { get; private set; }
    public bool TwoFactorEnabled { get; private set; }
    public bool HasTwoFactorPhone { get; private set; }
    public string? TwoFactorPhoneMasked { get; private set; }
    public bool SmsTwoFactorAvailable { get; private set; }
    public bool HasRecoveryCodes { get; private set; }
    public IReadOnlyList<string>? LastRecoveryCodes { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(Email) && !string.IsNullOrEmpty(LocalEncryptionKeyB64);

    public string ServerBaseUrl =>
        !string.IsNullOrWhiteSpace(_endpoint?.BaseUrl)
            ? _endpoint!.BaseUrl.TrimEnd('/')
            : _serverOptions?.BaseUrl.TrimEnd('/')
                ?? _http.BaseAddress?.ToString().TrimEnd('/')
                ?? "";

    public string SyncHubUrl =>
        _endpoint is not null && !string.IsNullOrWhiteSpace(_endpoint.BaseUrl)
            ? _endpoint.SyncHubUrl
            : _serverOptions?.SyncHubUrl
                ?? (_http.BaseAddress is not null
                    ? new Uri(_http.BaseAddress, "sync-hub").ToString()
                    : "/sync-hub");

    public event Action? OnChanged;

    public ChatAuthService(
        HttpClient http,
        IOptions<AppServerOptions>? serverOptions = null,
        IAuthSessionPersistence? sessionPersistence = null,
        IAppServerEndpoint? endpoint = null,
        IClientDeviceId? deviceId = null)
    {
        _http = http;
        _serverOptions = serverOptions?.Value;
        _sessionPersistence = sessionPersistence;
        _endpoint = endpoint;
        _deviceId = deviceId;
    }

    public async Task LoadAsync()
    {
        await EnsureDeviceIdAsync();
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
            await EnsureDeviceIdAsync();
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
            await EnsureDeviceIdAsync();
            var payload = new { Email = email.Trim(), Code = code.Trim() };
            var resp = await _http.PostAsJsonAsync("api/auth/verify-code", payload);

            if (!resp.IsSuccessStatusCode)
            {
                string message = resp.StatusCode == HttpStatusCode.Unauthorized
                    ? "Invalid or expired login code. Request a new one."
                    : $"Could not verify code ({(int)resp.StatusCode}).";
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

    public async Task<(bool Success, string? Error)> ResetPasswordAsync(string email, string code)
    {
        try
        {
            await EnsureDeviceIdAsync();
            var payload = new { Email = email.Trim(), Code = code.Trim() };
            var resp = await _http.PostAsJsonAsync("api/auth/reset-password", payload);

            if (!resp.IsSuccessStatusCode)
            {
                string message = resp.StatusCode == HttpStatusCode.Unauthorized
                    ? "Invalid or expired login code. Request a new one."
                    : $"Could not reset password ({(int)resp.StatusCode}).";
                try
                {
                    var body = await ReadJsonOrNullAsync<ErrorMessageResponse>(resp);
                    if (!string.IsNullOrWhiteSpace(body?.Message))
                        message = body.Message;
                }
                catch { /* keep default */ }

                return (false, message);
            }

            LastRecoveryCodes = null;
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

    public async Task<AuthLoginResult> LoginWithPasswordAsync(string email, string password)
    {
        try
        {
            await EnsureDeviceIdAsync();
            var payload = new { Email = email.Trim(), Password = password, RememberDevice = true };
            var resp = await _http.PostAsJsonAsync("api/auth/login-password", payload);

            if (!resp.IsSuccessStatusCode)
            {
                string message = "Invalid email or password.";
                try
                {
                    var body = await ReadJsonOrNullAsync<ErrorMessageResponse>(resp);
                    if (!string.IsNullOrWhiteSpace(body?.Message))
                        message = body.Message;
                }
                catch { /* keep default */ }

                return AuthLoginResult.Fail(message);
            }

            var login = await ReadJsonOrNullAsync<PasswordLoginResponse>(resp);
            if (login?.RequiresTwoFactor == true && !string.IsNullOrWhiteSpace(login.ChallengeId))
            {
                return AuthLoginResult.NeedSecondFactor(
                    login.ChallengeId,
                    login.Methods ?? new[] { "email" },
                    login.MaskedPhone);
            }

            await LoadAsync();
            return IsAuthenticated
                ? AuthLoginResult.Ok()
                : AuthLoginResult.Fail("Signed in on the server, but the session could not be loaded.");
        }
        catch (Exception ex)
        {
            return AuthLoginResult.Fail("Network error: " + ex.Message);
        }
    }

    public async Task<AuthLoginResult> VerifyTwoFactorAsync(string challengeId, string code, string method)
    {
        try
        {
            await EnsureDeviceIdAsync();
            var payload = new { ChallengeId = challengeId, Code = code, Method = method, RememberDevice = true };
            var resp = await _http.PostAsJsonAsync("api/auth/2fa/verify", payload);

            if (!resp.IsSuccessStatusCode)
            {
                string message = "Incorrect or expired code.";
                try
                {
                    var body = await ReadJsonOrNullAsync<ErrorMessageResponse>(resp);
                    if (!string.IsNullOrWhiteSpace(body?.Message))
                        message = body.Message;
                }
                catch { /* keep default */ }

                return AuthLoginResult.Fail(message);
            }

            await LoadAsync();
            return IsAuthenticated
                ? AuthLoginResult.Ok()
                : AuthLoginResult.Fail("Signed in on the server, but the session could not be loaded.");
        }
        catch (Exception ex)
        {
            return AuthLoginResult.Fail("Network error: " + ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> SendTwoFactorAsync(string challengeId, string method)
    {
        try
        {
            var payload = new { ChallengeId = challengeId, Method = method };
            var resp = await _http.PostAsJsonAsync("api/auth/2fa/send", payload);
            if (resp.IsSuccessStatusCode)
                return (true, null);

            string message = "Could not send a code.";
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

    public async Task<(bool Success, string? Error)> SetTwoFactorEnabledAsync(bool enabled, string? currentPassword = null)
    {
        try
        {
            var payload = new { Enabled = enabled, CurrentPassword = currentPassword };
            var resp = await _http.PostAsJsonAsync("api/auth/2fa/settings", payload);
            if (!resp.IsSuccessStatusCode)
            {
                var apiError = await ReadApiErrorAsync(resp);
                if (!string.IsNullOrWhiteSpace(apiError))
                    return (false, apiError);

                if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    return (false,
                        "This login server does not support two-factor yet. Deploy the updated website, or point the desktop app at a local host running this branch.");
                }

                return (false, $"Could not update two-factor settings ({(int)resp.StatusCode}).");
            }

            TwoFactorEnabled = enabled;
            if (!enabled)
            {
                HasTwoFactorPhone = false;
                TwoFactorPhoneMasked = null;
                HasRecoveryCodes = false;
                LastRecoveryCodes = null;
            }
            else
            {
                var bodyOk = await ReadJsonOrNullAsync<TwoFactorSettingsResponse>(resp);
                LastRecoveryCodes = bodyOk?.RecoveryCodes;
                HasRecoveryCodes = LastRecoveryCodes is { Count: > 0 };
            }

            OnChanged?.Invoke();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "Network error: " + ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> EnrollTwoFactorPhoneAsync(string phone)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/auth/2fa/enroll-sms", new { Phone = phone });
            if (resp.IsSuccessStatusCode)
                return (true, null);

            string message = "Could not send an SMS code.";
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

    public async Task<(bool Success, string? Error)> ConfirmTwoFactorPhoneAsync(string phone, string code)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/auth/2fa/confirm-sms", new { Phone = phone, Code = code });
            if (!resp.IsSuccessStatusCode)
            {
                string message = "Incorrect or expired code.";
                try
                {
                    var body = await ReadJsonOrNullAsync<ErrorMessageResponse>(resp);
                    if (!string.IsNullOrWhiteSpace(body?.Message))
                        message = body.Message;
                }
                catch { /* keep default */ }

                return (false, message);
            }

            var bodyOk = await ReadJsonOrNullAsync<TwoFactorPhoneResponse>(resp);
            TwoFactorEnabled = true;
            HasTwoFactorPhone = true;
            TwoFactorPhoneMasked = bodyOk?.TwoFactorPhoneMasked;
            OnChanged?.Invoke();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "Network error: " + ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> RemoveTwoFactorPhoneAsync(string? currentPassword = null)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/auth/2fa/remove-phone", new { CurrentPassword = currentPassword });
            if (!resp.IsSuccessStatusCode)
            {
                string message = "Could not remove the phone number.";
                try
                {
                    var body = await ReadJsonOrNullAsync<ErrorMessageResponse>(resp);
                    if (!string.IsNullOrWhiteSpace(body?.Message))
                        message = body.Message;
                }
                catch { /* keep default */ }

                return (false, message);
            }

            HasTwoFactorPhone = false;
            TwoFactorPhoneMasked = null;
            OnChanged?.Invoke();
            return (true, null);
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

    public async Task<IReadOnlyList<AuthSessionInfo>> GetSessionsAsync()
    {
        try
        {
            await EnsureDeviceIdAsync();
            var resp = await _http.GetAsync("api/auth/sessions");
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<AuthSessionInfo>();
            var list = await ReadJsonOrNullAsync<List<AuthSessionInfo>>(resp);
            return list ?? (IReadOnlyList<AuthSessionInfo>)Array.Empty<AuthSessionInfo>();
        }
        catch
        {
            return Array.Empty<AuthSessionInfo>();
        }
    }

    public async Task<(bool Success, string? Error)> RevokeSessionAsync(string sessionId)
    {
        try
        {
            await EnsureDeviceIdAsync();
            var resp = await _http.PostAsJsonAsync("api/auth/sessions/revoke", new { Id = sessionId });
            if (resp.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadApiErrorAsync(resp) ?? "Could not sign out that device.");
        }
        catch (Exception ex)
        {
            return (false, "Network error: " + ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> RevokeOtherSessionsAsync()
    {
        try
        {
            await EnsureDeviceIdAsync();
            var resp = await _http.PostAsync("api/auth/sessions/revoke-others", null);
            if (resp.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadApiErrorAsync(resp) ?? "Could not sign out other devices.");
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

    public Task<string> GetOrCreateHistoryEncryptionKeyAsync() =>
        Task.FromResult(LocalEncryptionKeyB64 ?? string.Empty);

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
            if (keyResponse.StatusCode is HttpStatusCode.Unauthorized)
                return AuthFetchResult.Unauthorized;
            if (keyResponse.StatusCode is HttpStatusCode.Forbidden)
            {
                Console.WriteLine("[Auth] This device must sign in again before it can sync.");
                return AuthFetchResult.Unauthorized;
            }

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
            TwoFactorEnabled = me.TwoFactorEnabled;
            HasTwoFactorPhone = me.HasTwoFactorPhone;
            TwoFactorPhoneMasked = me.TwoFactorPhoneMasked;
            SmsTwoFactorAvailable = me.SmsTwoFactorAvailable;
            HasRecoveryCodes = me.HasRecoveryCodes;
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

    private static async Task<string?> ReadApiErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (TryStringProp(root, "message", out var message) || TryStringProp(root, "Message", out message))
                return message;

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in errors.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            return item.GetString();
                    }
                }
            }

            if (TryStringProp(root, "detail", out var detail))
                return detail;
            if (TryStringProp(root, "title", out var title))
                return title;
        }
        catch
        {
            // Keep the caller fallback.
        }

        return null;
    }

    private static bool TryStringProp(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private void ClearAuthState()
    {
        Email = null;
        UserId = null;
        LocalEncryptionKeyB64 = null;
        HasPassword = false;
        TwoFactorEnabled = false;
        HasTwoFactorPhone = false;
        TwoFactorPhoneMasked = null;
        SmsTwoFactorAvailable = false;
        HasRecoveryCodes = false;
        LastRecoveryCodes = null;
    }

    /// <summary>
    /// Warm the cached device id so the first app-origin request can attach headers
    /// without waiting on JS/storage. The handler, not DefaultRequestHeaders, sends them.
    /// </summary>
    private async Task EnsureDeviceIdAsync()
    {
        if (_deviceId is null)
            return;
        try
        {
            await _deviceId.GetOrCreateAsync();
        }
        catch
        {
            // Old clients / JS not ready: omit the header rather than blocking login.
        }
    }

    private record UserMeResponse(
        string? Email,
        string? Id,
        bool HasLocalEncryptionKey,
        bool HasPassword = false,
        bool TwoFactorEnabled = false,
        bool HasTwoFactorPhone = false,
        string? TwoFactorPhoneMasked = null,
        bool SmsTwoFactorAvailable = false,
        bool HasRecoveryCodes = false);
    private record TwoFactorSettingsResponse(bool Success, bool TwoFactorEnabled, string[]? RecoveryCodes);
    private record PasswordLoginResponse(
        bool Success,
        bool RequiresTwoFactor = false,
        string? ChallengeId = null,
        string[]? Methods = null,
        string? MaskedPhone = null,
        string? PreferredMethod = null);
    private record TwoFactorPhoneResponse(string? TwoFactorPhoneMasked);
    private record EncryptionKeyResponse(string? Key);
    private record ErrorMessageResponse(string? Message);
}