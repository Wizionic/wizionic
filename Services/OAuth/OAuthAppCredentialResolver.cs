using App.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace App.Services.OAuth;

/// <summary>
/// Resolves OAuth app ClientId/Secret/Redirect from SQLite first, then appsettings/env.
/// Client secrets: try Data Protection unprotect; fall back to plaintext (SQL insert).
/// </summary>
public sealed class OAuthAppCredentialResolver
{
    private readonly AppDbContext _db;
    private readonly KeyProtectionService _protector;
    private readonly OAuthOptions _options;
    private readonly ILogger<OAuthAppCredentialResolver> _log;

    public OAuthAppCredentialResolver(
        AppDbContext db,
        KeyProtectionService protector,
        IOptions<OAuthOptions> options,
        ILogger<OAuthAppCredentialResolver> log)
    {
        _db = db;
        _protector = protector;
        _options = options.Value;
        _log = log;
    }

    public async Task<ResolvedOAuthCredentials?> ResolveAsync(string providerId, CancellationToken ct = default)
    {
        providerId = (providerId ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(providerId))
            return null;

        var row = await _db.OAuthProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProviderId == providerId && p.Enabled, ct);

        if (row is not null
            && !string.IsNullOrWhiteSpace(row.ClientId)
            && !string.IsNullOrWhiteSpace(row.ClientSecretProtected)
            && !string.IsNullOrWhiteSpace(row.RedirectUri))
        {
            var secret = ResolveSecret(row.ClientSecretProtected);
            if (!string.IsNullOrEmpty(secret))
            {
                return new ResolvedOAuthCredentials(
                    providerId,
                    row.ClientId.Trim(),
                    secret,
                    row.RedirectUri.Trim(),
                    row.AuthorizeUrl,
                    row.TokenUrl,
                    Source: "database");
            }
        }

        // Fallback: IOptions / environment (local dev)
        var cfg = providerId switch
        {
            "google" => _options.Google,
            "github" => _options.GitHub,
            "notion" => _options.Notion,
            "stripe" => _options.Stripe,
            _ => null
        };

        if (cfg is null
            || string.IsNullOrWhiteSpace(cfg.ClientId)
            || string.IsNullOrWhiteSpace(cfg.ClientSecret)
            || string.IsNullOrWhiteSpace(cfg.RedirectUri))
            return null;

        return new ResolvedOAuthCredentials(
            providerId,
            cfg.ClientId.Trim(),
            cfg.ClientSecret.Trim(),
            cfg.RedirectUri.Trim(),
            AuthorizeUrl: null,
            TokenUrl: null,
            Source: "config");
    }

    public async Task<bool> IsConfiguredAsync(string providerId, CancellationToken ct = default) =>
        await ResolveAsync(providerId, ct) is not null;

    /// <summary>Unprotect DP ciphertext; if that fails, treat value as plaintext (manual SQL insert).</summary>
    private string ResolveSecret(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return "";

        var unprotected = _protector.Unprotect(stored);
        // KeyProtectionService returns "" on decrypt failure → use raw column (SQL plaintext insert).
        if (string.IsNullOrEmpty(unprotected))
        {
            _log.LogDebug("OAuth secret dual-read: treating column as plaintext (len={Len})", stored.Length);
            return stored;
        }

        return unprotected;
    }
}

public sealed record ResolvedOAuthCredentials(
    string ProviderId,
    string ClientId,
    string ClientSecret,
    string RedirectUri,
    string? AuthorizeUrl,
    string? TokenUrl,
    string Source);
