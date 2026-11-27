using System.Text.Json;
using System.Text.Json.Serialization;
using App.Services;

namespace App.Apis;

/// <summary>
/// Cached, trimmed models.dev metadata for client provider templates and enrichment.
/// Public data only; no user keys or chat content pass through these routes.
/// </summary>
public static class ModelCatalogEndpoints
{
    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapModelCatalogApis(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/model-catalog").AllowAnonymous();

        group.MapGet("/providers", async (ModelsDevCatalogService catalog, CancellationToken ct) =>
        {
            try
            {
                var providers = await catalog.GetProvidersAsync(ct);
                return Results.Json(providers, ResponseJson);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(
                    detail: "models.dev is unavailable right now. Try again shortly.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/provider/{id}", async (
            string id,
            ModelsDevCatalogService catalog,
            CancellationToken ct) =>
        {
            try
            {
                var provider = await catalog.GetProviderAsync(id, ct);
                return provider == null
                    ? Results.NotFound()
                    : Results.Json(provider, ResponseJson);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(
                    detail: "models.dev is unavailable right now. Try again shortly.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/models", async (ModelsDevCatalogService catalog, CancellationToken ct) =>
        {
            try
            {
                var models = await catalog.GetModelsAsync(ct);
                return Results.Json(models, ResponseJson);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(
                    detail: "models.dev is unavailable right now. Try again shortly.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/aliases", async (ModelsDevCatalogService catalog, CancellationToken ct) =>
        {
            try
            {
                var aliases = await catalog.GetAliasesAsync(ct);
                return Results.Json(aliases, ResponseJson);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(
                    detail: "models.dev is unavailable right now. Try again shortly.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return endpoints;
    }
}
