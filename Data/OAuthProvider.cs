namespace App.Data;

/// <summary>
/// App-level OAuth application credentials (one row per provider, e.g. github / google).
/// Not per-user tokens — those stay on the client in IKeyStore.
/// </summary>
public class OAuthProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable key: github, google, notion, stripe (lowercase).</summary>
    public string ProviderId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string ClientId { get; set; } = "";

    /// <summary>
    /// Client secret at rest. Prefer Data Protection ciphertext; plaintext SQL inserts
    /// are accepted via dual-read in the credential resolver.
    /// </summary>
    public string ClientSecretProtected { get; set; } = "";

    public string RedirectUri { get; set; } = "";

    /// <summary>Optional authorize URL override; null uses broker defaults.</summary>
    public string? AuthorizeUrl { get; set; }

    /// <summary>Optional token URL override; null uses broker defaults.</summary>
    public string? TokenUrl { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string? Notes { get; set; }
}
