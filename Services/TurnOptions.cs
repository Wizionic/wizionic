namespace App.Services;

/// <summary>
/// Cloudflare Realtime TURN. Secrets belong in env / user-secrets, not git.
/// </summary>
public sealed class TurnOptions
{
    public const string SectionName = "Turn";

    public int TtlSeconds { get; set; } = 86400;

    public CloudflareTurnOptions Cloudflare { get; set; } = new();
}

public sealed class CloudflareTurnOptions
{
    public string TokenId { get; set; } = "";
    public string ApiToken { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TokenId) && !string.IsNullOrWhiteSpace(ApiToken);
}
