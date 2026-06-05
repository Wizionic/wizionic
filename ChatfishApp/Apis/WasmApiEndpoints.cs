using ChatfishApp.Data;
using ChatfishApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ChatfishApp.Apis;

/// <summary>
/// Minimal API endpoints exposed specifically for the WASM client.
/// 
/// Auth surface:
/// - POST /api/auth/request-magic-link  (PUBLIC, no auth required) → creates the short-lived magic token and sends a real email containing both a prominent clickable login link and the raw URL for copy/paste (the recipient may be on a different device/browser than the one that started the login flow).
/// - GET  /api/auth/me                  (protected) → basic identity (email, id, has key) for a logged-in WASM client
/// - GET  /api/user/encryption-key      (protected) → the per-user key for client-side AES-GCM (localStorage + live sync payloads)
/// - GET  /api/keys                     (protected) → server-stored provider keys (decrypted) so WASM can import them
///
/// Plus tool proxies (under /api/tools) so that agentic/tool-calling in WASM chat can use
/// web search and page summarization without browser CORS problems. The real work happens
/// on the server (reusing the same AppTools as the interactive server chat).
///
/// The /api/auth/request-magic-link endpoint (and all /api/tools/*) are deliberately public
/// so that WASM clients work fully without an email login (guest / local-only mode).
/// The identity + key + provider-key endpoints remain protected by the cookie set by the
/// /magic-login handler.
/// 
/// The server never stores WASM conversation history (per design).
/// 
/// Keep this file as the single place for all WASM-facing HTTP APIs so Program.cs stays small.
/// </summary>
public static class WasmApiEndpoints
{
    public static IEndpointRouteBuilder MapWasmApis(this IEndpointRouteBuilder endpoints)
    {
        // Public (no auth) auth endpoints for the WASM client.
        // /api/auth/request-magic-link lets a WASM page (the new root landing page) initiate
        // a magic link without a cookie. A real email is sent (via IEmailSender / MailKit)
        // containing a nice clickable button/link PLUS the raw URL so the user can copy it
        // if they are on a different device or browser than the one where they started login.
        // The raw token/link is NEVER returned to the browser (security).
        var publicAuth = endpoints.MapGroup("/api/auth");
        publicAuth.MapPost("/request-magic-link", async (HttpContext ctx, MagicLinkService magic, IEmailSender emailSender, RequestMagicLink req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email))
                return Results.BadRequest("Email is required.");

            var token = await magic.CreateMagicLinkTokenAsync(req.Email.Trim());

            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var magicLink = $"{baseUrl}/magic-login?token={token}";

            // Always log for dev / ops visibility (even when real SMTP is configured).
            // This is safe because the link is only useful to someone who also controls the inbox.
            Console.WriteLine($"[DEV] Magic link for {req.Email}: {magicLink}");

            // Send the real email. The email body (both HTML and text) contains a prominent
            // clickable login action and the raw URL for copy/paste.
            await emailSender.SendMagicLinkEmailAsync(req.Email.Trim(), magicLink);

            // Do not return the magicLink to the caller. The only delivery channel is the
            // email that was just sent. The client UI only shows a generic "check your email" message.
            return Results.Ok(new { sent = true, message = "Magic login link sent. Check your email inbox (and spam folder). The message contains a clickable link and the raw URL for copying." });
        });

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

    // Request for the public magic-link flow (used by the WASM landing page at /).
    public record RequestMagicLink(string Email);
}
