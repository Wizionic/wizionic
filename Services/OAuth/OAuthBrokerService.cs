using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using App.Services.Connectors;

namespace App.Services.OAuth;

/// <summary>
/// Host-side OAuth 2.0 broker: builds authorize URLs, exchanges codes, refreshes tokens.
/// Client secrets never leave the server. Credentials resolve from SQLite then config.
/// </summary>
public sealed class OAuthBrokerService
{
    private readonly OAuthSessionStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OAuthAppCredentialResolver _credentials;
    private readonly ConnectorCatalogService _catalog;
    private readonly ILogger<OAuthBrokerService> _log;

    private static readonly Dictionary<string, string[]> BuiltInConnectorScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail"] =
        [
            "https://www.googleapis.com/auth/gmail.readonly",
            "https://www.googleapis.com/auth/gmail.send",
            "https://www.googleapis.com/auth/gmail.modify",
            "openid",
            "email",
            "profile"
        ],
        ["google-calendar"] =
        [
            "https://www.googleapis.com/auth/calendar",
            "https://www.googleapis.com/auth/calendar.events",
            "openid",
            "email",
            "profile"
        ],
        ["github"] = ["repo", "read:user", "user:email"],
        ["notion"] = [],
        ["stripe"] = ["read_write"]
    };

    public OAuthBrokerService(
        OAuthSessionStore store,
        IHttpClientFactory httpClientFactory,
        OAuthAppCredentialResolver credentials,
        ConnectorCatalogService catalog,
        ILogger<OAuthBrokerService> log)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _catalog = catalog;
        _log = log;
    }

    public Task<bool> IsProviderConfiguredAsync(string provider, CancellationToken ct = default) =>
        _credentials.IsConfiguredAsync(provider, ct);

    public async Task<(string? Error, string? AuthorizeUrl)> StartAuthAsync(
        string provider,
        string connectorId,
        string? returnBaseUrl,
        string? requestOrigin,
        CancellationToken ct = default)
    {
        provider = (provider ?? "").Trim().ToLowerInvariant();
        connectorId = (connectorId ?? "").Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(connectorId))
            return ("Missing provider or connector id.", null);

        if (!await IsAllowedConnectorAsync(provider, connectorId, ct))
            return ($"Unknown connector '{connectorId}' for provider '{provider}'.", null);

        var cfg = await _credentials.ResolveAsync(provider, ct);
        if (cfg is null)
            return ($"OAuth provider '{provider}' is not configured on the server.", null);

        var redirectUri = OAuthRedirectResolver.Resolve(provider, requestOrigin, cfg.RedirectUri);
        if (string.IsNullOrWhiteSpace(redirectUri))
            return ("Redirect URI is not configured.", null);

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var scopes = await ResolveScopesAsync(connectorId, ct);

        _store.PutPending(new OAuthPendingAuth
        {
            State = state,
            Provider = provider,
            ConnectorId = connectorId,
            CodeVerifier = verifier,
            ReturnBaseUrl = returnBaseUrl,
            RedirectUri = redirectUri,
            Scopes = scopes
        });

        var authorizeUrl = provider switch
        {
            "google" => BuildGoogleAuthorizeUrl(cfg.ClientId, redirectUri, state, challenge, scopes),
            "github" => BuildGitHubAuthorizeUrl(cfg.ClientId, redirectUri, state, scopes),
            "notion" => BuildNotionAuthorizeUrl(cfg.ClientId, redirectUri, state),
            "stripe" => BuildStripeAuthorizeUrl(cfg.ClientId, redirectUri, state, scopes),
            _ => null
        };

        if (authorizeUrl is null)
            return ($"Provider '{provider}' is not supported.", null);

        return (null, authorizeUrl);
    }

    public async Task<(string? Error, string? ClientRedirect)> CompleteCallbackAsync(
        string provider,
        string? code,
        string? state,
        string? error,
        string? errorDescription,
        CancellationToken ct = default)
    {
        provider = (provider ?? "").Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(error))
            return ($"Provider error: {error} {errorDescription}".Trim(), null);

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return ("Missing code or state.", null);

        var pending = _store.TakePending(state);
        if (pending is null)
            return ("Invalid or expired OAuth state. Please try connecting again.", null);

        if (!string.Equals(pending.Provider, provider, StringComparison.OrdinalIgnoreCase))
            return ("OAuth provider mismatch.", null);

        var cfg = await _credentials.ResolveAsync(provider, ct);
        if (cfg is null)
            return ("Provider not configured.", null);

        var redirectUri = string.IsNullOrWhiteSpace(pending.RedirectUri)
            ? cfg.RedirectUri
            : pending.RedirectUri;
        try
        {
            var tokens = await ExchangeCodeAsync(provider, cfg, code, redirectUri, pending.CodeVerifier, ct);
            if (tokens is null)
                return ("Token exchange failed.", null);

            var accountLabel = await FetchAccountLabelAsync(provider, tokens.AccessToken, ct)
                               ?? tokens.AccountLabel;

            var sessionId = _store.PutSession(new OAuthSessionResult
            {
                Provider = provider,
                ConnectorId = pending.ConnectorId,
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresAtUtc = tokens.ExpiresAtUtc,
                TokenType = tokens.TokenType,
                Scope = tokens.Scope,
                AccountLabel = accountLabel
            });

            var baseUrl = string.IsNullOrWhiteSpace(pending.ReturnBaseUrl)
                ? "/"
                : pending.ReturnBaseUrl.Trim();
            var isCustomScheme = Uri.TryCreate(baseUrl, UriKind.Absolute, out var retUri)
                && !string.Equals(retUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(retUri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            if (!isCustomScheme)
            {
                baseUrl = baseUrl.TrimEnd('/');
                if (!baseUrl.Contains("/tools", StringComparison.OrdinalIgnoreCase)
                    && !baseUrl.Contains("/api/oauth/done", StringComparison.OrdinalIgnoreCase))
                    baseUrl += "/tools";
            }
            var sep = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            var clientRedirect =
                $"{baseUrl}{sep}oauth_session={Uri.EscapeDataString(sessionId)}&oauth_connector={Uri.EscapeDataString(pending.ConnectorId)}";
            return (null, clientRedirect);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OAuth callback failed for {Provider}/{Connector}", provider, pending.ConnectorId);
            return ($"OAuth failed: {ex.Message}", null);
        }
    }

    public OAuthSessionResult? TakeSession(string sessionId) =>
        _store.TakeSession(sessionId);

    public async Task<(string? Error, OAuthSessionResult? Tokens)> RefreshAsync(
        string provider,
        string refreshToken,
        CancellationToken ct = default)
    {
        provider = (provider ?? "").Trim().ToLowerInvariant();
        var cfg = await _credentials.ResolveAsync(provider, ct);
        if (cfg is null)
            return ("Provider not configured.", null);
        if (string.IsNullOrWhiteSpace(refreshToken))
            return ("Missing refresh token.", null);

        try
        {
            var tokens = await RefreshTokenAsync(provider, cfg, refreshToken, ct);
            if (tokens is null)
                return ("Refresh failed.", null);
            return (null, tokens);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OAuth refresh failed for {Provider}", provider);
            return (ex.Message, null);
        }
    }

    private async Task<bool> IsAllowedConnectorAsync(string provider, string connectorId, CancellationToken ct)
    {
        if (IsBuiltInConnector(provider, connectorId))
            return true;

        // Dynamic catalog: connector row must exist and point at this provider.
        var cat = await _catalog.GetByConnectorIdAsync(connectorId, ct);
        if (cat is null)
            return false;
        return string.Equals(cat.OAuthProviderId, provider, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuiltInConnector(string provider, string connectorId) =>
        (provider, connectorId) switch
        {
            ("google", "gmail") => true,
            ("google", "google-calendar") => true,
            ("github", "github") => true,
            ("notion", "notion") => true,
            ("stripe", "stripe") => true,
            _ => false
        };

    private async Task<string[]> ResolveScopesAsync(string connectorId, CancellationToken ct)
    {
        var cat = await _catalog.GetByConnectorIdAsync(connectorId, ct);
        if (cat?.Scopes is { Length: > 0 })
            return cat.Scopes;
        return BuiltInConnectorScopes.GetValueOrDefault(connectorId) ?? Array.Empty<string>();
    }

    private static string BuildGoogleAuthorizeUrl(
        string clientId, string redirectUri, string state, string challenge, string[] scopes)
    {
        var scope = string.Join(' ', scopes);
        var q = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" + ToQuery(q);
    }

    private static string BuildGitHubAuthorizeUrl(string clientId, string redirectUri, string state, string[] scopes)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state
        };
        return "https://github.com/login/oauth/authorize?" + ToQuery(q);
    }

    private static string BuildNotionAuthorizeUrl(string clientId, string redirectUri, string state)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["owner"] = "user",
            ["redirect_uri"] = redirectUri,
            ["state"] = state
        };
        return "https://api.notion.com/v1/oauth/authorize?" + ToQuery(q);
    }

    private static string BuildStripeAuthorizeUrl(string clientId, string redirectUri, string state, string[] scopes)
    {
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["scope"] = scopes.Length > 0 ? string.Join(' ', scopes) : "read_write",
            ["redirect_uri"] = redirectUri,
            ["state"] = state
        };
        return "https://connect.stripe.com/oauth/authorize?" + ToQuery(q);
    }

    private async Task<OAuthSessionResult?> ExchangeCodeAsync(
        string provider,
        ResolvedOAuthCredentials cfg,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct)
    {
        using var http = _httpClientFactory.CreateClient("oauth");
        return provider switch
        {
            "google" => await ExchangeGoogleAsync(http, cfg, code, redirectUri, codeVerifier, ct),
            "github" => await ExchangeGitHubAsync(http, cfg, code, redirectUri, ct),
            "notion" => await ExchangeNotionAsync(http, cfg, code, redirectUri, ct),
            "stripe" => await ExchangeStripeAsync(http, cfg, code, ct),
            _ => null
        };
    }

    private async Task<OAuthSessionResult?> RefreshTokenAsync(
        string provider,
        ResolvedOAuthCredentials cfg,
        string refreshToken,
        CancellationToken ct)
    {
        using var http = _httpClientFactory.CreateClient("oauth");
        if (provider == "google")
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId,
                ["client_secret"] = cfg.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            };
            using var resp = await http.PostAsync(
                "https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(form),
                ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Google refresh failed: {Status} {Body}", resp.StatusCode, Trunc(body));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var access = root.GetProperty("access_token").GetString() ?? "";
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            return new OAuthSessionResult
            {
                Provider = "google",
                AccessToken = access,
                RefreshToken = refreshToken,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : "Bearer",
                Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null
            };
        }

        return null;
    }

    private static async Task<OAuthSessionResult?> ExchangeGoogleAsync(
        HttpClient http, ResolvedOAuthCredentials cfg, string code, string redirectUri, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = cfg.ClientId,
            ["client_secret"] = cfg.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier
        };
        using var resp = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google token exchange failed: {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        return new OAuthSessionResult
        {
            Provider = "google",
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : "Bearer",
            Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null
        };
    }

    private static async Task<OAuthSessionResult?> ExchangeGitHubAsync(
        HttpClient http, ResolvedOAuthCredentials cfg, string code, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = cfg.ClientId,
            ["client_secret"] = cfg.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(form)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub token exchange failed: {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
            throw new InvalidOperationException(err.GetString() ?? "GitHub OAuth error");

        return new OAuthSessionResult
        {
            Provider = "github",
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : "Bearer",
            Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null
        };
    }

    private static async Task<OAuthSessionResult?> ExchangeNotionAsync(
        HttpClient http, ResolvedOAuthCredentials cfg, string code, string redirectUri, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            grant_type = "authorization_code",
            code,
            redirect_uri = redirectUri
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.notion.com/v1/oauth/token")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.ClientId}:{cfg.ClientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Notion token exchange failed: {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string? label = null;
        if (root.TryGetProperty("owner", out var owner) &&
            owner.TryGetProperty("user", out var user) &&
            user.TryGetProperty("name", out var name))
            label = name.GetString();

        return new OAuthSessionResult
        {
            Provider = "notion",
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : "Bearer",
            AccountLabel = label
        };
    }

    private static async Task<OAuthSessionResult?> ExchangeStripeAsync(
        HttpClient http, ResolvedOAuthCredentials cfg, string code, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_secret"] = cfg.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code"
        };
        using var resp = await http.PostAsync("https://connect.stripe.com/oauth/token", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe token exchange failed: {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new OAuthSessionResult
        {
            Provider = "stripe",
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : "Bearer",
            Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null,
            AccountLabel = root.TryGetProperty("stripe_user_id", out var uid) ? uid.GetString() : null
        };
    }

    private async Task<string?> FetchAccountLabelAsync(string provider, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        try
        {
            using var http = _httpClientFactory.CreateClient("oauth");
            if (provider == "google")
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("email", out var email))
                    return email.GetString();
            }
            else if (provider == "github")
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Headers.UserAgent.ParseAdd("Wizionic-OAuth");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("login", out var login))
                    return login.GetString();
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Account label fetch failed for {Provider}", provider);
        }

        return null;
    }

    private static string ToQuery(Dictionary<string, string> q) =>
        string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Trunc(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 200 ? s : s[..200] + "…");
}
