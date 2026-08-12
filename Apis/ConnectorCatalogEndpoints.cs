using App.Services.Connectors;
using App.Services.OAuth;

namespace App.Apis;

/// <summary>
/// Public connector marketplace catalog (no secrets).
/// </summary>
public static class ConnectorCatalogEndpoints
{
    public static IEndpointRouteBuilder MapConnectorCatalogApis(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/connectors");

        group.MapGet("/catalog", async (ConnectorCatalogService catalog, CancellationToken ct) =>
        {
            var items = await catalog.GetEnabledCatalogAsync(ct);
            return Results.Ok(items);
        });

        group.MapGet("/catalog/featured", async (ConnectorCatalogService catalog, CancellationToken ct) =>
        {
            var items = await catalog.GetFeaturedAsync(ct);
            return Results.Ok(items);
        });

        // Which OAuth providers have credentials (DB or config). No secrets returned.
        group.MapGet("/oauth-status", async (OAuthAppCredentialResolver resolver, CancellationToken ct) =>
        {
            var providers = new[] { "google", "github", "notion", "stripe" };
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in providers)
                map[p] = await resolver.IsConfiguredAsync(p, ct);
            return Results.Ok(map);
        });

        return endpoints;
    }
}
