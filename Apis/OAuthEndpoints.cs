using App.Services.OAuth;
using System.Text.Json.Serialization;

namespace App.Apis;

/// <summary>
/// Host OAuth broker endpoints for OpenAPI connectors.
/// Tokens are handed off once via short-lived sessions; never stored in SQLite.
/// </summary>
public static class OAuthEndpoints
{
    public static IEndpointRouteBuilder MapOAuthApis(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/oauth");

        // Start requires a signed-in session; secrets stay server-side.
        group.MapGet("/{provider}/start", async (
            string provider,
            string? connector,
            string? returnUrl,
            HttpContext ctx,
            OAuthBrokerService broker,
            CancellationToken ct) =>
        {
            var connectorId = string.IsNullOrWhiteSpace(connector) ? provider : connector;
            var request = ctx.Request;
            var baseOrigin = $"{request.Scheme}://{request.Host.Value}";
            var ret = string.IsNullOrWhiteSpace(returnUrl) ? baseOrigin : returnUrl.Trim();

            var (error, authorizeUrl) = await broker.StartAuthAsync(
                provider, connectorId!, ret, requestOrigin: baseOrigin, ct);
            if (error is not null || authorizeUrl is null)
                return Results.BadRequest(new { message = error ?? "Could not start OAuth." });

            return Results.Redirect(authorizeUrl);
        }).RequireAuthorization();

        group.MapGet("/{provider}/callback", async (
            string provider,
            string? code,
            string? state,
            string? error,
            string? error_description,
            OAuthBrokerService broker,
            CancellationToken ct) =>
        {
            var (err, clientRedirect) = await broker.CompleteCallbackAsync(
                provider, code, state, error, error_description, ct);

            if (err is not null || clientRedirect is null)
            {
                var msg = Uri.EscapeDataString(err ?? "OAuth failed");
                return Results.Redirect($"/tools?oauth_error={msg}");
            }

            return Results.Redirect(clientRedirect);
        });

        // One-shot token pickup for the client Tools page.
        group.MapGet("/session/{sessionId}", (string sessionId, OAuthBrokerService broker) =>
        {
            var session = broker.TakeSession(sessionId);
            if (session is null)
                return Results.NotFound(new { message = "Session expired or already used." });

            return Results.Ok(new OAuthSessionResponse(
                session.ConnectorId,
                session.Provider,
                session.AccessToken,
                session.RefreshToken,
                session.ExpiresAtUtc,
                session.TokenType,
                session.Scope,
                session.AccountLabel));
        });

        // HTTPS landing page after OAuth (MAUI embedded WebView cannot reliably open wizionic://).
        // Query: oauth_session + oauth_connector — the app intercepts this URL and redeems the session.
        group.MapGet("/done", (HttpRequest req) =>
        {
            var session = req.Query["oauth_session"].ToString();
            var connector = req.Query["oauth_connector"].ToString();
            var err = req.Query["oauth_error"].ToString();
            var ok = string.IsNullOrEmpty(err) && !string.IsNullOrEmpty(session);
            var title = ok ? "Connected" : "Connection issue";
            var body = ok
                ? "You are signed in. Return to Wizionic — this tab can close automatically."
                : (string.IsNullOrEmpty(err) ? "Missing session. Try connecting again from Tools." : err);
            var html = $$"""
                <!DOCTYPE html>
                <html lang="en"><head>
                <meta charset="utf-8"/>
                <meta name="viewport" content="width=device-width, initial-scale=1"/>
                <title>Wizionic — {{title}}</title>
                <style>
                  body{font-family:system-ui,sans-serif;display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0;background:#f8fafc;color:#0f172a}
                  .card{background:#fff;border:1px solid #e2e8f0;border-radius:16px;padding:2rem 2.25rem;max-width:420px;text-align:center;box-shadow:0 8px 30px rgba(15,23,42,.06)}
                  h1{font-size:1.25rem;margin:0 0 .5rem}
                  p{color:#64748b;margin:0;line-height:1.45}
                </style>
                </head><body>
                <div class="card">
                  <h1>{{title}}</h1>
                  <p>{{body}}</p>
                </div>
                </body></html>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        });

        group.MapPost("/{provider}/refresh", async (
            string provider,
            OAuthRefreshRequest req,
            OAuthBrokerService broker,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RefreshToken))
                return Results.BadRequest(new { message = "RefreshToken is required." });

            var (error, tokens) = await broker.RefreshAsync(provider, req.RefreshToken, ct);
            if (error is not null || tokens is null)
                return Results.BadRequest(new { message = error ?? "Refresh failed." });

            return Results.Ok(new OAuthSessionResponse(
                tokens.ConnectorId,
                tokens.Provider,
                tokens.AccessToken,
                tokens.RefreshToken,
                tokens.ExpiresAtUtc,
                tokens.TokenType,
                tokens.Scope,
                tokens.AccountLabel));
        });

        // Status for UI (which providers have client ids configured). DB then config.
        group.MapGet("/status", async (OAuthBrokerService broker, CancellationToken ct) =>
        {
            return Results.Ok(new
            {
                google = await broker.IsProviderConfiguredAsync("google", ct),
                github = await broker.IsProviderConfiguredAsync("github", ct),
                notion = await broker.IsProviderConfiguredAsync("notion", ct),
                stripe = await broker.IsProviderConfiguredAsync("stripe", ct)
            });
        });

        return endpoints;
    }

    public sealed record OAuthSessionResponse(
        string ConnectorId,
        string Provider,
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAtUtc,
        string? TokenType,
        string? Scope,
        string? AccountLabel);

    public sealed record OAuthRefreshRequest(
        [property: JsonPropertyName("refreshToken")] string? RefreshToken);
}
