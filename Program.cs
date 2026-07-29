using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ChatfishApp.Data;
using ChatfishApp.Components;
using ChatfishApp.Services;

using ChatfishApp.Apis;
using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Homeserver;
using ChatfishApp.Core.Storage;
using ChatfishApp.Core.Sync;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Claims;
using ChatfishApp.Shared.Services;

// Windows Service / homeserver: content root must be the published app directory (not System32).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Optional local homeserver overrides live outside the replaceable app folder so updates
// never wipe connection strings or other durable settings.
var homeserverSettingsPath = HomeserverPaths.AppsettingsPath;
if (File.Exists(homeserverSettingsPath))
{
    builder.Configuration.AddJsonFile(homeserverSettingsPath, optional: false, reloadOnChange: true);
    Console.WriteLine($"[Homeserver] Loaded config from {homeserverSettingsPath}");
}

// Allow running as a Windows Service or Linux systemd unit
// (no-op when started interactively / in Docker).
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();


var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
// Ensure SQLite parent directory exists (e.g. "data/chatfish.db" for local/dev).
EnsureSqliteDirectory(connectionString);

// Homeserver / HTTP-only: cookie Secure=Always breaks login on plain http://localhost.
var homeserverHttpCookies = builder.Configuration.GetValue("Homeserver:AllowHttpCookies", false)
    || File.Exists(homeserverSettingsPath);
builder.Services.AddDbContext<ChatfishDbContext>(options =>
{
    options.UseSqlite(connectionString);
    // Ignore this in dev so that model changes during active development (e.g. after removing legacy tables)
    // don't hard-crash startup before the corresponding migration is created/applied.
    // We always add a proper migration for model changes (see RemoveLegacyServerChatHistoryTables).
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// NOTE: Removed the previous hardcoded Groq HttpClient + embedded API key (security issue).
// Keys are now per-user via ProviderKeyService (WASM clients call providers directly; server only proxies tools + serves keys).

builder.Services.AddScoped<MagicLinkService>();
builder.Services.AddScoped<ProviderKeyService>();
builder.Services.AddSingleton<DevicePresenceService>();

// Email sending (used for real magic link delivery).
// Brevo HTTP API is now the primary sender (SMTP is blocked by some hosts e.g. Railway).
// The old EmailSender (SMTP/MailKit) is left in place and can be swapped back by changing the registration below.
// Configure non-secret parts under the "Brevo" section in appsettings.
// The secret BREVO_API_KEY must come from environment variable.
builder.Services.Configure<BrevoEmailOptions>(builder.Configuration.GetSection("Brevo"));

// Named HttpClient for Brevo transactional email API
builder.Services.AddHttpClient("brevo", client =>
{
    client.BaseAddress = new Uri("https://api.brevo.com/v3/");
    client.DefaultRequestHeaders.Add("accept", "application/json");
});

builder.Services.AddScoped<IEmailSender, BrevoEmailSender>();

// Force SmtpUser / SmtpPass from environment variables (Email__SmtpUser / Email__SmtpPass)
// when those variables are defined in the process environment. This ensures the values
// the user explicitly set via $env: (or system/user environment variables) take
// precedence over anything in appsettings or dotnet user-secrets.
builder.Services.PostConfigure<EmailOptions>(opts =>
{
    // Check Process (current $env:), then persistent User scope (for when people use
    // [System.Environment]::SetEnvironmentVariable(..., "User") ), then the colon form.
    string? envUser = Environment.GetEnvironmentVariable("Email__SmtpUser")
                   ?? Environment.GetEnvironmentVariable("Email__SmtpUser", EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable("Email:SmtpUser");
    if (envUser is not null)
    {
        opts.SmtpUser = envUser;
    }

    string? envPass = Environment.GetEnvironmentVariable("Email__SmtpPass")
                   ?? Environment.GetEnvironmentVariable("Email__SmtpPass", EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable("Email:SmtpPass");
    if (envPass is not null)
    {
        opts.SmtpPass = envPass;
    }
});

builder.Services.AddSingleton<ThemeService>();
// CORS-restricted AI providers (e.g. Zyphra) — proxied through the backend using server-side keys.
builder.Services.Configure<AiProviderProxyOptions>(builder.Configuration.GetSection(AiProviderProxyOptions.SectionName));
builder.Services.AddHttpClient("ai-proxy", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton<AiProviderProxyService>();

// Shared state for WASM sidebar toggle (used by WasmTopBar in WasmLayout for /chat etc.)
// Must be registered here (main app DI) so that server-side rendering of WASM pages (layout + topbar)
// can provide the service. The Client's DI also registers it for the interactive WASM runtime.
builder.Services.AddSingleton<ChatfishApp.Client.Services.SidebarState>();
builder.Services.AddSingleton<ChatfishApp.Core.UI.ISidebarState>(sp => sp.GetRequiredService<ChatfishApp.Client.Services.SidebarState>());
builder.Services.AddSingleton<ChatfishApp.Client.Services.BrowserPanelState>();
builder.Services.AddSingleton<ChatfishApp.Core.UI.IBrowserPanelState>(sp => sp.GetRequiredService<ChatfishApp.Client.Services.BrowserPanelState>());
builder.Services.AddSingleton<ChatfishApp.Client.Services.ChatPanelState>();
builder.Services.AddSingleton<ChatfishApp.Core.UI.IChatPanelState>(sp => sp.GetRequiredService<ChatfishApp.Client.Services.ChatPanelState>());
builder.Services.AddSingleton<ChatfishApp.Client.Services.NotesPanelState>();
builder.Services.AddSingleton<ChatfishApp.Core.UI.INotesPanelState>(sp => sp.GetRequiredService<ChatfishApp.Client.Services.NotesPanelState>());
builder.Services.AddSingleton<ChatfishApp.Shared.Services.NavLayoutService>();
builder.Services.AddSingleton<ChatfishApp.Core.UI.INavLayoutState>(sp => sp.GetRequiredService<ChatfishApp.Shared.Services.NavLayoutService>());
builder.Services.AddSingleton<ChatfishApp.Core.Browser.IBrowserAgentService, ChatfishApp.Client.Services.NullBrowserAgentService>();
builder.Services.AddSingleton<ChatfishApp.Core.Browser.IBrowserTabManager, ChatfishApp.Client.Services.NullBrowserTabManager>();
builder.Services.AddSingleton<ChatfishApp.Core.Browser.IBrowserOverlaySync, ChatfishApp.Client.Services.NullBrowserOverlaySync>();
builder.Services.AddSingleton<ChatfishApp.Core.Browser.IBrowserStore, ChatfishApp.Client.Services.NullBrowserStore>();
builder.Services.AddSingleton<ChatfishApp.Core.Browser.IBrowserSidebarStore, ChatfishApp.Client.Services.NullBrowserSidebarStore>();
builder.Services.AddSingleton<ChatfishApp.Client.Services.BrowserSidePanelState>();
builder.Services.AddSingleton<ChatfishApp.Core.UI.IBrowserSidePanelState>(sp => sp.GetRequiredService<ChatfishApp.Client.Services.BrowserSidePanelState>());
builder.Services.AddSingleton<ChatfishApp.Core.Browser.IBrowserSideAgentService, ChatfishApp.Client.Services.NullBrowserSideAgentService>();
builder.Services.AddSingleton<ChatfishApp.Core.Browser.IPwaDetector, ChatfishApp.Client.Services.NullPwaDetector>();

// HttpClient + shared auth/sync stubs for server-side rendering of WASM layout/components (AppLayout, SyncConnectionBootstrap, etc.).
// The interactive WASM runtime (Client/Program.cs) provides its own scoped services when the page becomes interactive.
// Do not register WasmSyncService or other client-only implementations here — they can interfere with WASM bootstrap.
builder.Services.AddScoped(sp =>
{
    var navigationManager = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navigationManager.BaseUri) };
});
builder.Services.AddScoped<ChatAuthService>();
builder.Services.AddScoped<IAuthService>(sp => sp.GetRequiredService<ChatAuthService>());
builder.Services.AddScoped<NullSyncService>();
builder.Services.AddScoped<ISyncService>(sp => sp.GetRequiredService<NullSyncService>());
builder.Services.AddScoped<INotesSyncBridge>(sp => sp.GetRequiredService<NullSyncService>());

// Data Protection persists the ASP.NET cookie auth ticket encryption key ring in SQLite.
// Users.LocalEncryptionKey (WASM/MAUI IndexedDB crypto) is stored as plaintext base64 and is
// NOT wrapped with Data Protection — see LocalEncryptionKeyService.
// Forwarded headers so that when deployed behind a TLS-terminating reverse proxy / load balancer
// (Railway, Render, Cloudflare, nginx, etc.) we correctly see Scheme=https, the real Host, and client IP.
// This fixes mixed-content redirect URLs (http://chatfish.me?ReturnUrl=...) for auth challenges on /api/*
// and ensures magic links and cookies are generated with the proper https scheme.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Cloud/container platforms set X-Forwarded-* from arbitrary edge IPs; trust them all for scheme/host.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Persist DataProtection keys *in the SQLite database* (via the existing ChatfishDbContext).
// This is the user's explicit preference: avoid any filesystem/volume concerns for keys at the hosting provider.
// Auth cookies (and the per-user LocalEncryptionKey protector) will survive restarts, deploys, and sleep/wake
// as long as chatfish.db itself persists. The DP keys table is small.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ChatfishDbContext>()
    .SetApplicationName("Chatfish");
builder.Services.AddSingleton<KeyProtectionService>();
builder.Services.AddSingleton<ChatfishApp.Core.Update.IUpdateService>(_ => ChatfishApp.Shared.Services.NullUpdateService.Instance);
builder.Services.AddSingleton<ChatfishApp.Core.Homeserver.IHomeserverInstallService>(
    _ => ChatfishApp.Shared.Services.NullHomeserverInstallService.Instance);
builder.Services.AddSingleton<ChatfishApp.Core.Configuration.IChatfishServerEndpoint>(
    _ => ChatfishApp.Shared.Services.NullChatfishServerEndpoint.Instance);
builder.Services.AddSingleton<ChatfishApp.Core.Setup.ISetupWizardHost>(
    _ => ChatfishApp.Shared.Services.NullSetupWizardHost.Instance);
builder.Services.AddSingleton<ChatfishApp.Core.Lemonade.ILemonadeInstallService>(
    _ => ChatfishApp.Shared.Services.NullLemonadeInstallService.Instance);
builder.Services.AddSingleton<ChatfishApp.Core.Ollama.IOllamaInstallService>(
    _ => ChatfishApp.Shared.Services.NullOllamaInstallService.Instance);
builder.Services.AddSingleton<ChatfishApp.Core.UI.IUrlEmbedOverlay>(
    _ => ChatfishApp.Shared.Services.NullUrlEmbedOverlay.Instance);

builder.Services.AddAuthentication("ChatfishAuth")
    .AddCookie("ChatfishAuth", options =>
    {
        // Root (/) is now the WASM landing page that handles both "login with email" and
        // "continue without login" (guest/local mode). The cookie middleware therefore
        // redirects unauthenticated users who hit a [Authorize] page to "/".
        options.LoginPath = "/";
        options.LogoutPath = "/logout";

        // Persistent login: cookie survives browser restarts until explicit sign-out.
        // Sign-in must pass IsPersistent=true or the browser stores a session cookie (cleared on quit).
        // Sliding renewal extends the expiry on each authenticated request so active users stay logged in.
        // Persisted DP keys (above) prevent invalidation on server restarts/sleeps.
        options.ExpireTimeSpan = TimeSpan.FromDays(365 * 10);
        options.SlidingExpiration = true;

        // Production hardening: always require Secure (https), and Lax SameSite so magic-link
        // email clicks (cross-site top-level navigation) still work while protecting against CSRF on APIs.
        options.Cookie.HttpOnly = true;
        // Development and local homeserver use plain http — Secure cookies would never be stored.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || homeserverHttpCookies
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // WASM HttpClient calls /api/* with fetch; a 302 to "/" is followed and returns HTML,
        // which then crashes JSON parsing during WASM startup. APIs get 401/403 instead.
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api")
                    || ctx.Request.Path.StartsWithSegments("/hubs")
                    || ctx.Request.Path.StartsWithSegments("/sync-hub"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api")
                    || ctx.Request.Path.StartsWithSegments("/hubs")
                    || ctx.Request.Path.StartsWithSegments("/sync-hub"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSignalR();


var app = builder.Build();

// Must be the first middleware to rewrite context from X-Forwarded-* headers before
// HSTS, HTTPS redirection, authentication, or anything that inspects Scheme/Host.
app.UseForwardedHeaders();

// Diagnostic at startup so it's obvious whether Email__Smtp* env vars (or user-secrets)
// were picked up for SMTP credentials. Never logs secret values.
{
    var emailSection = builder.Configuration.GetSection("Email");
    var sectionUser = emailSection["SmtpUser"] ?? string.Empty;
    var sectionPass = emailSection["SmtpPass"] ?? string.Empty;

    string liveUser = Environment.GetEnvironmentVariable("Email__SmtpUser")
                   ?? Environment.GetEnvironmentVariable("Email__SmtpUser", EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable("Email:SmtpUser")
                   ?? "(not set)";
    string livePass = Environment.GetEnvironmentVariable("Email__SmtpPass")
                   ?? Environment.GetEnvironmentVariable("Email__SmtpPass", EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable("Email:SmtpPass")
                   ?? "(not set)";
    int livePassLen = livePass == "(not set)" ? 0 : livePass.Length;
    string livePassPreview = livePassLen >= 8 ? livePass.Substring(0,4) + "..." + livePass.Substring(livePassLen-4) : livePass;

    bool userFromEnv = liveUser != "(not set)";
    bool passFromEnv = livePass != "(not set)";

    bool hasUser = !string.IsNullOrWhiteSpace(sectionUser);
    bool hasPass = !string.IsNullOrWhiteSpace(sectionPass);

    Console.WriteLine($"[Email] SMTP configured: host={emailSection["SmtpHost"] ?? "<none>"} port={emailSection["SmtpPort"] ?? "587"} hasUser={hasUser} hasPass={hasPass} user=\"{sectionUser}\" from={emailSection["From"] ?? "<default>"}");
    Console.WriteLine($"[Email] LIVE ENV AT STARTUP: Email__SmtpUser='{liveUser}' | Email__SmtpPass len={livePassLen} preview={livePassPreview} (fromEnv user:{userFromEnv} pass:{passFromEnv})");
}

// Brevo HTTP API diagnostics (new primary email path)
{
    string? brevoKey = Environment.GetEnvironmentVariable("BREVO_API_KEY")
                    ?? Environment.GetEnvironmentVariable("Email__BrevoApiKey")
                    ?? Environment.GetEnvironmentVariable("Email__BrevoApiKey", EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable("Email:BrevoApiKey");

    var brevoSection = builder.Configuration.GetSection("Brevo");
    string from = brevoSection["From"] ?? builder.Configuration["Email:From"] ?? "(default)";
    string senderName = brevoSection["SenderName"] ?? "Chatfish";

    bool keyPresent = !string.IsNullOrWhiteSpace(brevoKey);
    int keyLen = brevoKey?.Length ?? 0;
    string keyPreview = keyLen >= 8 ? brevoKey!.Substring(0, 4) + "..." + brevoKey!.Substring(keyLen - 4) : (brevoKey ?? "(not set)");

    Console.WriteLine($"[Brevo] Configured: from={from} senderName={senderName} keyPresent={keyPresent} keyLen={keyLen} preview={keyPreview}");
    if (!keyPresent)
    {
        Console.WriteLine("[Brevo] WARNING: No BREVO_API_KEY (or Email__BrevoApiKey) found. Emails will not be sent via Brevo until the env var is set.");
    }
}

// Proxied AI provider diagnostics (keys are never logged).
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AiProviderProxyService>().LogStartupDiagnostics();
}

app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatfishDbContext>();
    db.Database.Migrate();

    var dbPath = ResolveSqlitePath(connectionString) ?? Path.GetFullPath("chatfish.db");
    long dbSizeBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : -1;
    int dpKeyCount = db.DataProtectionKeys.Count();
    Console.WriteLine($"[Auth] Persistence: chatfish.db path={dbPath} sizeBytes={dbSizeBytes} dataProtectionKeyCount={dpKeyCount}");
    if (dpKeyCount == 0)
    {
        Console.WriteLine("[Auth] WARNING: No DataProtection keys in DB. Auth cookies will not survive server restarts until keys are generated.");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // HSTS is counterproductive for local HTTP homeserver installs.
    if (!homeserverHttpCookies)
    {
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// Skip HTTPS redirection for local homeserver (HTTP-only on localhost).
if (!homeserverHttpCookies)
    app.UseHttpsRedirection();

app.UseAntiforgery();

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (path is "/service-worker.js" or "/service-worker-assets.js")
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            return Task.CompletedTask;
        });
    }
    await next();
});

app.MapStaticAssets();

// Serve Velopack / Linux installer files from the volume-mounted releases directory.
// MapStaticAssets() only serves files in the build-time manifest, so volume-mounted
// paths need an explicit endpoint.
static string? ResolveReleaseFile(string relativePath)
{
    // Prefer container layout used in production, then local wwwroot / content root.
    var candidates = new[]
    {
        Path.GetFullPath(Path.Combine("/app/wwwroot/releases", relativePath)),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "releases", relativePath)),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot", "releases", relativePath)),
    };

    var releasesRoots = new[]
    {
        Path.GetFullPath("/app/wwwroot/releases"),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "releases")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot", "releases")),
    };

    foreach (var safePath in candidates)
    {
        var allowed = false;
        foreach (var root in releasesRoots)
        {
            if (safePath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || string.Equals(safePath, root, StringComparison.Ordinal))
            {
                allowed = true;
                break;
            }
        }

        if (!allowed)
            continue;
        if (File.Exists(safePath))
            return safePath;
    }

    return null;
}

static string ReleaseContentType(string path) =>
    Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".exe" => "application/octet-stream",
        ".nupkg" => "application/octet-stream",
        ".appimage" => "application/octet-stream",
        ".deb" => "application/vnd.debian.binary-package",
        ".zip" => "application/zip",
        ".json" => "application/json",
        ".sh" => "text/x-shellscript; charset=utf-8",
        _ => "application/octet-stream"
    };

// GET + HEAD: install.sh uses existence checks; HEAD must not 405.
app.MapMethods("/releases/{**path}", new[] { "GET", "HEAD" }, (string path, HttpContext ctx) =>
{
    var safePath = ResolveReleaseFile(path);
    if (safePath is null)
        return Results.NotFound();

    var contentType = ReleaseContentType(safePath);
    if (HttpMethods.IsHead(ctx.Request.Method))
    {
        var info = new FileInfo(safePath);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = info.Length;
        return Results.Empty;
    }

    return Results.File(safePath, contentType);
});

// curl -fsSL https://chatfish.me/install.sh | bash
app.MapMethods("/install.sh", new[] { "GET", "HEAD" }, (HttpContext ctx) =>
{
    var safePath = ResolveReleaseFile(Path.Combine("linux", "install.sh"));
    // Also accept site-root copy next to wwwroot (deploy-linux may scp here)
    if (safePath is null)
    {
        foreach (var candidate in new[]
                 {
                     "/app/wwwroot/install.sh",
                     Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "install.sh"),
                     Path.Combine(Directory.GetCurrentDirectory(), "install.sh"),
                 })
        {
            if (File.Exists(candidate))
            {
                safePath = candidate;
                break;
            }
        }
    }

    if (safePath is null)
        return Results.NotFound("Linux install script not found. Run deploy-linux.sh to publish it.");

    ctx.Response.Headers.CacheControl = "no-cache";
    var contentType = "text/x-shellscript; charset=utf-8";
    if (HttpMethods.IsHead(ctx.Request.Method))
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = new FileInfo(safePath).Length;
        return Results.Empty;
    }

    return Results.File(safePath, contentType, fileDownloadName: "install.sh");
});

app.MapGet("/magic-login", async (HttpContext ctx, string token, MagicLinkService magicLinks) =>
{
    var user = await magicLinks.ValidateMagicLinkAsync(token);

    if (user == null)
    {
        ctx.Response.Redirect("/");
        return;
    }

    await AuthSignInHelper.SignInUserAsync(ctx, user);

    // After successful magic-link sign-in, land on "/" so the WASM landing page can
    // immediately show the "Logged in as ..." state (with buttons to chat/settings).
    // The user then explicitly chooses to go to chat or settings.
    ctx.Response.Redirect("/");
});


app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("ChatfishAuth");
    // Return to the WASM root so the user sees the (now guest) landing page with
    // the option to log in again or continue without an account.
    ctx.Response.Redirect("/");
});

// WASM client APIs (kept in WasmApiEndpoints.cs so Program.cs stays small).
// All under /api + cookie auth. See Apis/WasmApiEndpoints.cs for the implementations.
app.MapWasmApis();
app.MapAiProxyApis();

// Live device presence + future WebRTC signaling hub for authenticated WASM clients.
// The hub itself is marked [Authorize] and relies on the ChatfishAuth cookie
// (same cookie the WASM client already sends for /api/auth/me etc.).
// Clients connect from the same origin, so cookies are sent automatically.
app.MapHub<ChatfishApp.Apis.SyncHub>("/sync-hub");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ChatfishApp.Client.WasmMarker).Assembly);


app.Run();

/// <summary>
/// Create the parent folder for a SQLite "Data Source=..." path so first-run
/// local/dev doesn't crash with "unable to open database file".
/// </summary>
static void EnsureSqliteDirectory(string? connectionString)
{
    var path = ResolveSqlitePath(connectionString);
    if (path is null)
        return;

    try
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DB] WARNING: could not create SQLite directory for '{path}': {ex.Message}");
    }
}

/// <summary>Extract absolute SQLite file path from a connection string, if present.</summary>
static string? ResolveSqlitePath(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
        return null;

    // "Data Source=data/chatfish.db" or "Data Source=./data/chatfish.db"
    const string prefix = "Data Source=";
    var idx = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
    if (idx < 0)
        return null;

    var path = connectionString[(idx + prefix.Length)..].Trim();
    // Strip trailing ";Mode=..." etc.
    var semi = path.IndexOf(';');
    if (semi >= 0)
        path = path[..semi].Trim();

    if (string.IsNullOrWhiteSpace(path))
        return null;

    try
    {
        return Path.GetFullPath(path);
    }
    catch
    {
        return path;
    }
}

