namespace App.Data;

/// <summary>
/// Marketplace catalog row (Featured tile): name, icon, scopes, OAuth provider link.
/// </summary>
public class Connector
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable id: gmail, google-calendar, github, …</summary>
    public string ConnectorId { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>0 = OpenApiOAuth, 1 = Mcp (see App.Core.Connectors.ConnectorKind).</summary>
    public int Kind { get; set; }

    /// <summary>Links to <see cref="OAuthProvider.ProviderId"/> (e.g. gmail → google).</summary>
    public string? OAuthProviderId { get; set; }

    /// <summary>JSON array of OAuth scopes for this connector.</summary>
    public string? ScopesJson { get; set; }

    public string? DocsUrl { get; set; }

    public bool Featured { get; set; } = true;
    public int SortOrder { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Glyph/emoji in the icon square when no image URL.</summary>
    public string IconText { get; set; } = "?";

    /// <summary>CSS background for the icon square (color or gradient).</summary>
    public string IconBackground { get; set; } = "#6b7280";

    /// <summary>Optional image URL (https or data:); when set, used instead of IconText.</summary>
    public string? IconImageUrl { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
