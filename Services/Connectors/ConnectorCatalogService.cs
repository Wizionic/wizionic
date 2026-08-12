using System.Text.Json;
using App.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Services.Connectors;

/// <summary>Reads marketplace connector catalog from SQLite (no secrets).</summary>
public sealed class ConnectorCatalogService
{
    private readonly AppDbContext _db;

    public ConnectorCatalogService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ConnectorCatalogDto>> GetEnabledCatalogAsync(CancellationToken ct = default)
    {
        var rows = await _db.Connectors.AsNoTracking()
            .Where(c => c.Enabled)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.DisplayName)
            .ToListAsync(ct);

        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<ConnectorCatalogDto>> GetFeaturedAsync(CancellationToken ct = default)
    {
        var rows = await _db.Connectors.AsNoTracking()
            .Where(c => c.Enabled && c.Featured)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.DisplayName)
            .ToListAsync(ct);

        return rows.Select(Map).ToList();
    }

    public async Task<ConnectorCatalogDto?> GetByConnectorIdAsync(string connectorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectorId))
            return null;

        var id = connectorId.Trim().ToLowerInvariant();
        var row = await _db.Connectors.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConnectorId == id && c.Enabled, ct);
        return row is null ? null : Map(row);
    }

    private static ConnectorCatalogDto Map(Connector c)
    {
        string[] scopes = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(c.ScopesJson))
        {
            try
            {
                scopes = JsonSerializer.Deserialize<string[]>(c.ScopesJson) ?? Array.Empty<string>();
            }
            catch
            {
                scopes = Array.Empty<string>();
            }
        }

        return new ConnectorCatalogDto(
            c.ConnectorId,
            c.DisplayName,
            c.Description,
            c.Kind,
            c.OAuthProviderId,
            scopes,
            c.DocsUrl,
            c.Featured,
            c.SortOrder,
            c.IconText,
            c.IconBackground,
            c.IconImageUrl);
    }
}

public sealed record ConnectorCatalogDto(
    string ConnectorId,
    string DisplayName,
    string Description,
    int Kind,
    string? OAuthProviderId,
    string[] Scopes,
    string? DocsUrl,
    bool Featured,
    int SortOrder,
    string IconText,
    string IconBackground,
    string? IconImageUrl);
