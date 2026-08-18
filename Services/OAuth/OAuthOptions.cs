namespace App.Services.OAuth;

/// <summary>Root configuration section "OAuth" in appsettings.</summary>
public sealed class OAuthOptions
{
    public const string SectionName = "OAuth";

    public OAuthProviderOptions Google { get; set; } = new();
    public OAuthProviderOptions GitHub { get; set; } = new();
    public OAuthProviderOptions Notion { get; set; } = new();
    public OAuthProviderOptions Stripe { get; set; } = new();
}

public sealed class OAuthProviderOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Absolute redirect URI registered with the provider (e.g. https://host/api/oauth/google/callback).</summary>
    public string RedirectUri { get; set; } = "";
}

/// <summary>Short-lived state for the authorization redirect (CSRF + PKCE).</summary>
public sealed class OAuthPendingAuth
{
    public string State { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ConnectorId { get; set; } = "";
    public string CodeVerifier { get; set; } = "";
    public string? ReturnBaseUrl { get; set; }
    /// <summary>Exact redirect_uri sent to the provider; must be reused on token exchange.</summary>
    public string RedirectUri { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string[] Scopes { get; set; } = Array.Empty<string>();
}

/// <summary>One-shot handoff of tokens to the client after callback.</summary>
public sealed class OAuthSessionResult
{
    public string SessionId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ConnectorId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string? TokenType { get; set; }
    public string? Scope { get; set; }
    public string? AccountLabel { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
