using ChatfishApp.Data;
using ChatfishApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ChatfishApp.Apis;

/// <summary>
/// Minimal API endpoints exposed specifically for the WASM client.
/// 
/// These are the "get some info from the database" surface for authenticated WASM:
/// - /api/auth/me           → basic identity (email, id, has key)
/// - /api/user/encryption-key → the per-user key for client-side AES-GCM (localStorage + live sync payloads)
/// - /api/keys              → server-stored provider keys (decrypted) so WASM can import them
///
/// Plus tool proxies (under /api/tools) so that agentic/tool-calling in WASM chat can use
/// web search and page summarization without browser CORS problems. The real work happens
/// on the server (reusing the same AppTools as the interactive server chat).
///
/// All under /api and protected by the existing cookie auth (for WASM users).
/// 
/// The server never stores WASM conversation history (per design).
/// 
/// Keep this file as the single place for all WASM-facing HTTP APIs so Program.cs stays small.
/// </summary>
public static class WasmApiEndpoints
{
    public static IEndpointRouteBuilder MapWasmApis(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api").RequireAuthorization();

        // Tool endpoints are intentionally *not* under the authorized group.
        // They are app-level free tools (search, summarize) and should work even
        // for unauthenticated WASM users (e.g. pure local Ollama without login).
        // The main /api/* (auth/me, keys, encryption-key) remain protected.
        var toolsGroup = endpoints.MapGroup("/api/tools");

        group.MapGet("/auth/me", async (ClaimsPrincipal user, KeyProtectionService protector, ChatfishDbContext db) =>
        {
            var email = user.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            // Lightweight check: does the DB row for this email have a (protected) LocalEncryptionKey?
            // The actual unprotected key bytes are returned by the dedicated endpoint below.
            var hasKey = await db.Users.AsNoTracking()
                .AnyAsync(u => u.Email == email && u.LocalEncryptionKey != null);

            return Results.Ok(new
            {
                Email = email,
                Id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                HasLocalEncryptionKey = hasKey
            });
        });

        group.MapGet("/user/encryption-key", async (ClaimsPrincipal user, KeyProtectionService protector, ChatfishDbContext db) =>
        {
            var email = user.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (u == null || string.IsNullOrEmpty(u.LocalEncryptionKey))
                return Results.NotFound("No encryption key provisioned for this user yet.");

            var plaintextKey = protector.Unprotect(u.LocalEncryptionKey);
            if (string.IsNullOrEmpty(plaintextKey))
                return Results.Problem("Failed to unprotect encryption key.");

            return Results.Ok(new { Key = plaintextKey });
        });

        group.MapGet("/keys", async (ProviderKeyService keyService, KeyProtectionService protector) =>
        {
            // Returns the user's server-side provider keys (decrypted on the server)
            // so an authenticated WASM client can import them for its own direct
            // browser-to-provider calls. The WASM will store its local copy encrypted
            // with the user's LocalEncryptionKey (fetched from the same DB).
            var keys = await keyService.GetUserKeysAsync();
            var result = keys.Select(k => new
            {
                k.ProviderId,
                k.Enabled,
                Key = string.IsNullOrEmpty(k.Key) ? null : protector.Unprotect(k.Key) // plaintext only for this authenticated response
            });
            return Results.Ok(result);
        });

        // Tool proxies for WASM agentic mode.
        // These allow the browser (WASM) to use web search and page summarization
        // without CORS errors (the browser would otherwise be blocked when calling
        // DuckDuckGo or Jina directly). The real HTTP work happens on the server
        // (using the exact same AppTools code as the interactive server chat).
        // Results flow back to the model through the normal tool-calling loop.
        // 
        // Note: these are intentionally on a non-authorized subgroup so that
        // even local/unauthenticated WASM users (pure Ollama etc.) can use agentic tools.
        toolsGroup.MapPost("/web-search", async (WebSearchRequest req) =>
        {
            return await ChatfishApp.Services.Tools.AppTools.SearchWeb(req.Query, req.MaxResults ?? 5);
        });

        toolsGroup.MapPost("/summarize-url", async (SummarizeUrlRequest req) =>
        {
            return await ChatfishApp.Services.Tools.AppTools.SummarizeUrl(req.Url);
        });

        toolsGroup.MapPost("/get-current-weather", async (WeatherRequest req) =>
        {
            return await ChatfishApp.Services.Tools.AppTools.GetCurrentWeather(req.Latitude, req.Longitude, req.Units ?? "celsius", req.ForecastDays ?? 0);
        });

        return endpoints;
    }

    // Request shapes for the tool proxy endpoints.
    public record WebSearchRequest(string Query, int? MaxResults = 5);
    public record SummarizeUrlRequest(string Url);
    public record WeatherRequest(double Latitude, double Longitude, string? Units = "celsius", int? ForecastDays = 0);
}
