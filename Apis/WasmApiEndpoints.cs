using ChatfishApp.Data;
using ChatfishApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json.Serialization;


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

            var loginCode = await magic.CreateMagicLinkTokenAsync(req.Email.Trim());

            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var magicLink = $"{baseUrl}/magic-login?token={Uri.EscapeDataString(loginCode)}";

            Console.WriteLine($"[DEV] Login code for {req.Email}: {loginCode} (link: {magicLink})");

            await emailSender.SendLoginEmailAsync(req.Email.Trim(), loginCode, magicLink);

            return Results.Ok(new
            {
                sent = true,
                message = "Login code sent. Check your email inbox (and spam folder). The message contains a copy/paste code for the app and a web login link."
            });
        });

        publicAuth.MapPost("/verify-code", async (HttpContext ctx, MagicLinkService magic, VerifyLoginCode req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest("Email and code are required.");

            var user = await magic.ValidateLoginCodeAsync(req.Email.Trim(), req.Code.Trim());
            if (user == null)
                return Results.Unauthorized();

            await AuthSignInHelper.SignInUserAsync(ctx, user);

            return Results.Ok(new
            {
                success = true,
                email = user.Email,
                message = "Signed in successfully."
            });
        });

        publicAuth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await AuthSignInHelper.SignOutUserAsync(ctx);
            return Results.Ok(new { signedOut = true });
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

            var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
            if (u == null)
                return Results.Unauthorized();

            string? plaintextKey = null;

            if (!string.IsNullOrEmpty(u.LocalEncryptionKey))
            {
                plaintextKey = protector.Unprotect(u.LocalEncryptionKey);
            }

            if (string.IsNullOrEmpty(plaintextKey))
            {
                // Either first time, or the previously protected value can no longer be unprotected
                // (e.g. DataProtection key ring was lost during a transition, dev clean, or hosting restart before DB persistence).
                // Re-provision a fresh key. This lets the login flow complete and the WASM client see an authenticated user.
                // Note: any client-side IndexedDB history encrypted with the *old* key bytes will no longer decrypt on this device
                // (or other devices that had the old key). For early users this is acceptable; they can re-login everywhere.
                var rawKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                u.LocalEncryptionKey = protector.Protect(rawKey);
                await db.SaveChangesAsync();
                plaintextKey = rawKey;

                Console.WriteLine($"[Auth] Re-provisioned fresh LocalEncryptionKey for {email} (previous value could not be unprotected).");
            }

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

        // Public MCP registry proxy + filter.
        // - Fetches from the official unauthenticated registry.
        // - Filters to ONLY servers that expose streamable-http or sse remotes (no stdio-only entries, since a web client cannot launch local processes).
        // - Deduplicates by server name, preferring the latest published version.
        // - Returns a small clean list the WASM Tools page can render with checkboxes.
        // Client (browser) calls this instead of hitting the registry directly to avoid CORS.
        toolsGroup.MapGet("/mcp-registry", async () =>
        {
            try
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                // Use a slightly higher limit to get a good selection after filtering + dedup.
                var raw = await hc.GetFromJsonAsync<RegistryResponse>("https://registry.modelcontextprotocol.io/v0/servers?limit=50");

                var results = new List<RemoteMcpServer>();

                if (raw?.Servers != null)
                {
                    // Group by canonical name, pick best (latest) entry per name.
                    var bestPerName = raw.Servers
                        .Where(e => e?.Server != null && !string.IsNullOrWhiteSpace(e.Server.Name))
                        .GroupBy(e => e.Server!.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(g =>
                        {
                            // Prefer entries explicitly marked isLatest, then highest version string (lexical is often fine for semver here).
                            return g.OrderByDescending(x => IsLatest(x))
                                    .ThenByDescending(x => x.Server!.Version ?? "")
                                    .First();
                        });

                    foreach (var entry in bestPerName)
                    {
                        var sv = entry.Server!;
                        // Only keep servers that publish at least one web-callable remote transport.
                        var remote = sv.Remotes?.FirstOrDefault(r =>
                            !string.IsNullOrWhiteSpace(r?.Url) &&
                            (string.Equals(r.Type, "streamable-http", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(r.Type, "sse", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(r.Type, "http", StringComparison.OrdinalIgnoreCase)));

                        if (remote == null) continue;

                        bool requiresAuth = remote.Headers?.Any(h => h is { IsRequired: true }) ?? false;

                        string? infoUrl = !string.IsNullOrWhiteSpace(sv.WebsiteUrl) ? sv.WebsiteUrl :
                                          (sv.Repository?.Url);

                        results.Add(new RemoteMcpServer(
                            Name: sv.Name,
                            Description: sv.Description ?? sv.Title ?? "",
                            RemoteUrl: remote.Url!,
                            Transport: remote.Type ?? "remote",
                            RequiresAuth: requiresAuth,
                            InfoUrl: infoUrl,
                            Version: sv.Version ?? ""
                        ));
                    }
                }

                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, title: "Failed to fetch MCP registry");
            }

            static bool IsLatest(RegistryEntry e)
            {
                // The key in _meta contains slashes; our DTO maps it via JsonPropertyName.
                return e?._meta?.Official?.IsLatest ?? false;
            }
        });

        return endpoints;
    }

    // Request shapes for the tool proxy endpoints.
    public record WebSearchRequest(string Query, int? MaxResults = 5);
    public record SummarizeUrlRequest(string Url);
    public record WeatherRequest(double Latitude, double Longitude, string? Units = "celsius", int? ForecastDays = 0);

    // Request for the public login flow (used by WASM and MAUI landing pages).
    public record RequestMagicLink(string Email);
    public record VerifyLoginCode(string Email, string Code);

    // --- MCP Registry proxy models (clean output for the Tools page) ---

    /// <summary>
    /// Clean, filtered representation of a remote-capable MCP server for the Tools UI.
    /// Only entries that have usable HTTP/SSE remotes are returned (stdio-only servers are dropped server-side).
    /// </summary>
    public record RemoteMcpServer(
        string Name,
        string Description,
        string RemoteUrl,
        string Transport,
        bool RequiresAuth,
        string? InfoUrl,
        string Version
    );

    // --- Raw registry deserialization shapes (match https://registry.modelcontextprotocol.io/v0/servers) ---

    public class RegistryResponse
    {
        public List<RegistryEntry> Servers { get; set; } = new();
        public object? Metadata { get; set; }
    }

    public class RegistryEntry
    {
        public RegistryServer? Server { get; set; }

        [JsonPropertyName("_meta")]
        public RegistryMeta? _meta { get; set; }
    }

    public class RegistryServer
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Version { get; set; }
        public string? WebsiteUrl { get; set; }

        public RegistryRepository? Repository { get; set; }
        public List<RegistryRemote>? Remotes { get; set; }
        // packages (stdio etc.) are intentionally ignored for the web client list
    }

    public class RegistryRepository
    {
        public string? Url { get; set; }
    }

    public class RegistryRemote
    {
        public string Type { get; set; } = string.Empty;
        public string? Url { get; set; }

        public List<RegistryHeader>? Headers { get; set; }
    }

    public class RegistryHeader
    {
        public string Name { get; set; } = string.Empty;
        public bool IsRequired { get; set; }
        public bool IsSecret { get; set; }
    }

    public class RegistryMeta
    {
        [JsonPropertyName("io.modelcontextprotocol.registry/official")]
        public OfficialMeta? Official { get; set; }
    }

    public class OfficialMeta
    {
        public bool IsLatest { get; set; }
    }
}
