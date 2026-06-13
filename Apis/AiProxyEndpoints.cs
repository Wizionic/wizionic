using ChatfishApp.Contracts;
using ChatfishApp.Services;

namespace ChatfishApp.Apis;

/// <summary>
/// Public AI provider proxy for WASM clients.
/// Routes chat completions through the backend for providers that block browser CORS.
/// Provider definitions and API keys are configured server-side only (appsettings + env vars).
/// </summary>
public static class AiProxyEndpoints
{
    public static IEndpointRouteBuilder MapAiProxyApis(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/proxy");

        group.MapGet("/providers", async (AiProviderProxyService proxy, CancellationToken ct) =>
        {
            var providers = await proxy.GetAvailableProvidersAsync(ct);
            return Results.Ok(new ProxiedProviderContracts.ProxyProvidersResponse { Providers = providers.ToList() });
        });

        group.MapPost("/chat", async (ProxiedProviderContracts.ProxyChatRequest request, AiProviderProxyService proxy, CancellationToken ct) =>
        {
            try
            {
                var json = await proxy.ProxyChatAsync(request, ct);
                return Results.Content(json, "application/json");
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        return endpoints;
    }
}