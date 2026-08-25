using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using App.Data;
using App.Components;
using App.Services;

using App.Apis;
using App.Core.Auth;
using App.Core.Homeserver;
using App.Core.Storage;
using App.Core.Sync;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Claims;
using App.Shared.Services;
using App.Services.OAuth;

// Windows Service / homeserver: content root must be the published app directory (not System32).
// For `dotnet run` / `dotnet watch` from the repo, keep the project directory so relative
// paths and Development config match the source layout.
var isHomeserverHost = IsRunningAsHomeserverHost();
var contentRoot = ResolveContentRoot(isHomeserverHost);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot
});

// Local homeserver overrides live outside the replaceable app folder so updates
// never wipe connection strings or other durable settings.
// IMPORTANT: only load them when THIS process is the installed Home Server host.
// If we load them during `dotnet run`/`dotnet watch`, the connection string points at
// %ProgramData%\Wizionic\Homeserver\data\homeserver.db — which is owned by the Windows
// Service (Administrators) and is often read-only for the interactive user, producing
// "attempt to write a readonly database" during Migrate(). Production Docker never has
// this file, so it always uses appsettings + the mounted /app/data volume.
var homeserverSettingsPath = HomeserverPaths.AppsettingsPath;
var homeserverConfigLoaded = false;
if (isHomeserverHost && File.Exists(homeserverSettingsPath))
{
    builder.Configuration.AddJsonFile(homeserverSettingsPath, optional: false, reloadOnChange: true);
    homeserverConfigLoaded = true;
    Console.WriteLine($"[Homeserver] Loaded config from {homeserverSettingsPath}");
}
else if (File.Exists(homeserverSettingsPath))
{
    Console.WriteLine(
        $"[Homeserver] Ignoring {homeserverSettingsPath} (not running as installed Home Server host). " +
        "Using project/appsettings connection string so dev runs do not touch the service database.");
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
// Ensure SQLite parent directory exists (e.g. "data/homeserver.db" for local/dev).
EnsureSqliteDirectory(connectionString);

// Homeserver / HTTP-only: cookie Secure=Always breaks login on plain http://localhost.
var homeserverHttpCookies = builder.Configuration.GetValue("Homeserver:AllowHttpCookies", false)
    || homeserverConfigLoaded;
builder.Services.AddDbContext<AppDbContext>(options =>
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
builder.Services.Configure<TwilioOptions>(builder.Configuration.GetSection(TwilioOptions.SectionName));
builder.Services.PostConfigure<TwilioOptions>(opts =>
{
    opts.AccountSid = FirstEnv("Twilio__AccountSid", "Twilio:AccountSid") ?? opts.AccountSid;
    opts.ApiKeySid = FirstEnv("Twilio__ApiKeySid", "Twilio:ApiKeySid") ?? opts.ApiKeySid;
    opts.ApiKeySecret = FirstEnv("Twilio__ApiKeySecret", "Twilio:ApiKeySecret") ?? opts.ApiKeySecret;
    opts.VerifyServiceSid = FirstEnv("Twilio__VerifyServiceSid", "Twilio:VerifyServiceSid") ?? opts.VerifyServiceSid;
});
builder.Services.AddSingleton<ITwilioVerifyService, TwilioVerifyService>();
builder.Services.AddScoped<TwoFactorAuthService>();
builder.Services.AddScoped<ProviderKeyService>();
builder.Services.AddSingleton<DevicePresenceService>();

// OAuth OpenAPI connector broker (client secrets stay on the host).
builder.Services.Configure<OAuthOptions>(builder.Configuration.GetSection(OAuthOptions.SectionName));
builder.Services.AddSingleton<OAuthSessionStore>();
builder.Services.AddScoped<App.Services.OAuth.OAuthAppCredentialResolver>();
builder.Services.AddScoped<App.Services.Connectors.ConnectorCatalogService>();
builder.Services.AddScoped<OAuthBrokerService>();
builder.Services.AddHttpClient("oauth", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("connector-proxy", c => c.Timeout = TimeSpan.FromSeconds(60));

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
builder.Services.AddSingleton<App.Client.Services.SidebarState>();
builder.Services.AddSingleton<App.Core.UI.ISidebarState>(sp => sp.GetRequiredService<App.Client.Services.SidebarState>());
builder.Services.AddSingleton<App.Client.Services.BrowserPanelState>();
builder.Services.AddSingleton<App.Core.UI.IBrowserPanelState>(sp => sp.GetRequiredService<App.Client.Services.BrowserPanelState>());
builder.Services.AddSingleton<App.Client.Services.ChatPanelState>();
builder.Services.AddSingleton<App.Core.UI.IChatPanelState>(sp => sp.GetRequiredService<App.Client.Services.ChatPanelState>());
builder.Services.AddSingleton<App.Client.Services.NotesPanelState>();
builder.Services.AddSingleton<App.Core.UI.INotesPanelState>(sp => sp.GetRequiredService<App.Client.Services.NotesPanelState>());
builder.Services.AddSingleton<App.Shared.Services.NavLayoutService>();
builder.Services.AddSingleton<App.Core.UI.INavLayoutState>(sp => sp.GetRequiredService<App.Shared.Services.NavLayoutService>());
// AppLayout → AppNavigationBootstrap injects IAppNavigation (host SSR + shared shell).
builder.Services.AddSingleton<App.Core.UI.IAppNavigation, App.Shared.Services.AppNavigation>();
builder.Services.AddSingleton<App.Core.Browser.IBrowserAgentService, App.Client.Services.NullBrowserAgentService>();
builder.Services.AddSingleton<App.Core.Browser.IBrowserTabManager, App.Client.Services.NullBrowserTabManager>();
builder.Services.AddSingleton<App.Core.Browser.IBrowserOverlaySync, App.Client.Services.NullBrowserOverlaySync>();
builder.Services.AddSingleton<App.Core.Browser.IBrowserStore, App.Client.Services.NullBrowserStore>();
builder.Services.AddSingleton<App.Core.Browser.IBrowserSidebarStore, App.Client.Services.NullBrowserSidebarStore>();
builder.Services.AddSingleton<App.Client.Services.BrowserSidePanelState>();
builder.Services.AddSingleton<App.Core.UI.IBrowserSidePanelState>(sp => sp.GetRequiredService<App.Client.Services.BrowserSidePanelState>());
builder.Services.AddSingleton<App.Core.Browser.IBrowserSideAgentService, App.Client.Services.NullBrowserSideAgentService>();
builder.Services.AddSingleton<App.Core.Browser.IPwaDetector, App.Client.Services.NullPwaDetector>();

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
// Parameterless: articles are embedded. Do not inject NavigationManager (scoped) here.
builder.Services.AddSingleton<App.Shared.Services.Help.HelpCatalogService>(_ => new App.Shared.Services.Help.HelpCatalogService());
builder.Services.AddSingleton<App.Core.Help.IHelpCatalog>(sp => sp.GetRequiredService<App.Shared.Services.Help.HelpCatalogService>());
builder.Services.AddSingleton<App.Shared.Services.Help.HelpOverlay>();
builder.Services.AddSingleton<App.Core.Help.IHelpOverlay>(sp => sp.GetRequiredService<App.Shared.Services.Help.HelpOverlay>());
builder.Services.AddScoped<NullSyncService>();
builder.Services.AddScoped<ISyncService>(sp => sp.GetRequiredService<NullSyncService>());
builder.Services.AddScoped<INotesSyncBridge>(sp => sp.GetRequiredService<NullSyncService>());
builder.Services.AddScoped<IGallerySyncBridge>(sp => sp.GetRequiredService<NullSyncService>());
builder.Services.AddScoped<ICalendarSyncBridge>(sp => sp.GetRequiredService<NullSyncService>());
builder.Services.AddScoped<ICalendarStore>(_ => App.Shared.Services.NullCalendarStore.Instance);
builder.Services.AddSingleton<IStorageQuotaService>(_ => NullStorageQuotaService.Instance);
builder.Services.AddSingleton<App.Core.Storage.ISettingsSyncStore>(
    _ => App.Shared.Services.NullSettingsSyncStore.Instance);

// Data Protection persists the ASP.NET cookie auth ticket encryption key ring in SQLite.
// Users.LocalEncryptionKey (WASM/MAUI IndexedDB crypto) is stored as plaintext base64 and is
// NOT wrapped with Data Protection — see LocalEncryptionKeyService.
// Forwarded headers so that when deployed behind a TLS-terminating reverse proxy / load balancer
// (Railway, Render, Cloudflare, nginx, etc.) we correctly see Scheme=https, the real Host, and client IP.
// This fixes mixed-content redirect URLs (http://wizionic.com?ReturnUrl=...) for auth challenges on /api/*
// and ensures magic links and cookies are generated with the proper https scheme.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Cloud/container platforms set X-Forwarded-* from arbitrary edge IPs; trust them all for scheme/host.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Persist DataProtection keys *in the SQLite database* (via the existing AppDbContext).
// This is the user's explicit preference: avoid any filesystem/volume concerns for keys at the hosting provider.
// Auth cookies (and the per-user LocalEncryptionKey protector) will survive restarts, deploys, and sleep/wake
// as long as homeserver.db itself persists. The DP keys table is small.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>()
    .SetApplicationName("Wizionic");
builder.Services.AddSingleton<KeyProtectionService>();
builder.Services.AddSingleton<App.Core.Update.IUpdateService>(_ => App.Shared.Services.NullUpdateService.Instance);
builder.Services.AddSingleton<App.Core.Homeserver.IHomeserverInstallService>(
    _ => App.Shared.Services.NullHomeserverInstallService.Instance);
builder.Services.AddSingleton<App.Core.Configuration.IAppServerEndpoint>(
    _ => App.Shared.Services.NullAppServerEndpoint.Instance);
builder.Services.AddSingleton<App.Core.UI.IAppRestartService>(
    _ => App.Shared.Services.NullAppRestartService.Instance);
builder.Services.AddSingleton<App.Core.UI.IDesktopShellService>(
    _ => App.Shared.Services.NullDesktopShellService.Instance);
builder.Services.AddSingleton<App.Core.Setup.ISetupWizardHost>(
    _ => App.Shared.Services.NullSetupWizardHost.Instance);
builder.Services.AddSingleton<App.Core.Lemonade.ILemonadeInstallService>(
    _ => App.Shared.Services.NullLemonadeInstallService.Instance);
builder.Services.AddSingleton<App.Core.Ollama.IOllamaInstallService>(
    _ => App.Shared.Services.NullOllamaInstallService.Instance);
builder.Services.AddSingleton<App.Core.UI.IUrlEmbedOverlay>(
    _ => App.Shared.Services.NullUrlEmbedOverlay.Instance);
// Shared layout (SetupWizard in AppLayout) injects IKeyStore. Real settings live in
// WASM/MAUI; the host only needs a no-op so SSR DI can construct those components.
builder.Services.AddSingleton<App.Core.Storage.IKeyStore>(
    _ => App.Shared.Services.NullKeyStore.Instance);
// Skills storage/runner live on WASM/MAUI clients. Host only needs no-ops for SSR DI.
builder.Services.AddSingleton<App.Core.Skills.ISkillStore>(
    _ => App.Shared.Services.Skills.NullSkillStore.Instance);
builder.Services.AddSingleton<App.Core.Skills.ISkillRunLogStore>(
    _ => App.Shared.Services.Skills.NullSkillRunLogStore.Instance);
builder.Services.AddSingleton<App.Core.Skills.ISkillRunner>(
    _ => App.Shared.Services.Skills.NullSkillRunner.Instance);
builder.Services.AddSingleton<App.Core.Workflows.IWorkflowStore>(
    _ => App.Shared.Services.Workflows.NullWorkflowStore.Instance);
builder.Services.AddSingleton<App.Core.Workflows.IWorkflowOrchestrator>(
    _ => App.Shared.Services.Workflows.NullWorkflowOrchestrator.Instance);

builder.Services.AddAuthentication("AppAuth")
    .AddCookie("AppAuth", options =>
    {
        // Root (/) is the WASM sign-in landing page. Cookie middleware redirects
        // unauthenticated users who hit a [Authorize] endpoint to "/".
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

if (isHomeserverHost)
{
    var port = 5150;
    if (!int.TryParse(HomeserverPaths.DefaultPort, out port))
        port = 5150;
    var configured = builder.Configuration["Kestrel:Endpoints:Http:Url"]
                     ?? builder.Configuration["Urls"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        var first = configured.Split(';', 2)[0].Trim();
        if (Uri.TryCreate(first, UriKind.Absolute, out var listenUri) && listenUri.Port > 0)
            port = listenUri.Port;
        else if (int.TryParse(builder.Configuration["Homeserver:Port"], out var parsed))
            port = parsed;
    }

    builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));
    Console.WriteLine($"[Homeserver] Kestrel ListenAnyIP({port})");
}

var app = builder.Build();

// Must be the first middleware to rewrite context from X-Forwarded-* headers before
// HSTS, HTTPS redirection, authentication, or anything that inspects Scheme/Host.
app.UseForwardedHeaders();

{
    var emailSection = builder.Configuration.GetSection("Email");
    Console.WriteLine($"[Email] SMTP host={emailSection["SmtpHost"] ?? "<none>"} configuredUser={!string.IsNullOrWhiteSpace(emailSection["SmtpUser"])}");

    var brevoKey = Environment.GetEnvironmentVariable("BREVO_API_KEY")
                ?? Environment.GetEnvironmentVariable("Email__BrevoApiKey");
    Console.WriteLine($"[Brevo] apiKeyConfigured={!string.IsNullOrWhiteSpace(brevoKey)}");

    var twilio = app.Services.GetRequiredService<ITwilioVerifyService>();
    Console.WriteLine($"[Twilio] verifyConfigured={twilio.IsConfigured}");
    Console.WriteLine("[Help] catalog=embedded");
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
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var dbPath = ResolveSqlitePath(connectionString) ?? Path.GetFullPath("homeserver.db");
    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex) when (ex.Message.Contains("readonly", StringComparison.OrdinalIgnoreCase)
                            || (ex.InnerException?.Message.Contains("readonly", StringComparison.OrdinalIgnoreCase) == true))
    {
        Console.WriteLine(
            $"[DB] FATAL: SQLite cannot write to '{dbPath}'. " +
            "If this is a local Home Server DB under ProgramData, it is likely owned by the Windows Service " +
            "and not writable from `dotnet run`. Dev builds should use appsettings (data/homeserver.db), not the service DB. " +
            "Do not delete production/homeserver homeserver.db — it holds user login + DataProtection keys.");
        throw;
    }

    long dbSizeBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : -1;
    int dpKeyCount = db.DataProtectionKeys.Count();
    Console.WriteLine($"[Auth] Persistence: homeserver.db path={dbPath} sizeBytes={dbSizeBytes} dataProtectionKeyCount={dpKeyCount}");
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

app.MapLegalDocuments();

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
        ".ps1" => "text/plain; charset=utf-8",
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

// curl -fsSL https://wizionic.com/install.sh | bash
app.MapMethods("/install.sh", new[] { "GET", "HEAD" }, (HttpContext ctx) =>
{
    var safePath = ResolveReleaseFile(Path.Combine("linux", "install.sh"));
    // Also accept site-root copy next to wwwroot (ops deploy may scp here)
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
        return Results.NotFound("Linux install script not found. Publish install.sh to the releases volume.");

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

// irm https://wizionic.com/install.ps1 | iex
app.MapMethods("/install.ps1", new[] { "GET", "HEAD" }, (HttpContext ctx) =>
{
    var safePath = ResolveReleaseFile(Path.Combine("windows", "install.ps1"));
    if (safePath is null)
    {
        foreach (var candidate in new[]
                 {
                     "/app/wwwroot/install.ps1",
                     Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "install.ps1"),
                     Path.Combine(Directory.GetCurrentDirectory(), "install.ps1"),
                     Path.Combine(Directory.GetCurrentDirectory(), "scripts", "install.ps1"),
                     Path.Combine(AppContext.BaseDirectory, "wwwroot", "install.ps1"),
                     Path.Combine(AppContext.BaseDirectory, "scripts", "install.ps1"),
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
        return Results.NotFound("Windows install script not found. Publish install.ps1 to the releases volume.");

    ctx.Response.Headers.CacheControl = "no-cache";
    var contentType = "text/plain; charset=utf-8";
    if (HttpMethods.IsHead(ctx.Request.Method))
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = new FileInfo(safePath).Length;
        return Results.Empty;
    }

    return Results.File(safePath, contentType, fileDownloadName: "install.ps1");
});

app.MapGet("/magic-login", async (HttpContext ctx, string token, MagicLinkService magicLinks, TwoFactorAuthService twoFactor) =>
{
    var user = await magicLinks.FindByMagicTokenAsync(token);

    if (user == null)
    {
        ctx.Response.Redirect("/");
        return;
    }

    // 2FA accounts: the email link only completes a password-verified challenge.
    if (user.TwoFactorEnabled && !twoFactor.HasLiveChallenge(user))
    {
        ctx.Response.Redirect("/?passwordRequired=1");
        return;
    }

    var ready = await magicLinks.ConsumeLoginTokenAsync(user);
    if (ready == null)
    {
        ctx.Response.Redirect("/");
        return;
    }

    twoFactor.ClearChallenge(ready);
    await twoFactor.PersistAsync();
    await AuthSignInHelper.SignInUserAsync(ctx, ready);

    // After successful magic-link sign-in, land on "/" so the WASM landing page can
    // immediately show the "Logged in as ..." state (with buttons to chat/settings).
    // The user then explicitly chooses to go to chat or settings.
    ctx.Response.Redirect("/");
});


app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("AppAuth");
    ctx.Response.Redirect("/");
});

// WASM client APIs (kept in WasmApiEndpoints.cs so Program.cs stays small).
// All under /api + cookie auth. See Apis/WasmApiEndpoints.cs for the implementations.
app.MapWasmApis();
app.MapAiProxyApis();
app.MapOAuthApis();
app.MapConnectorProxyApis();
app.MapConnectorCatalogApis();

// Live device presence + future WebRTC signaling hub for authenticated WASM clients.
// The hub itself is marked [Authorize] and relies on the AppAuth cookie
// (same cookie the WASM client already sends for /api/auth/me etc.).
// Clients connect from the same origin, so cookies are sent automatically.
app.MapHub<App.Apis.SyncHub>("/sync-hub");

app.MapRazorComponents<AppRoot>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(App.Client.WasmMarker).Assembly);


app.Run();

/// <summary>
/// True when this process is the installed Home Server host (published under
/// HomeserverPaths.AppDirectory, or APP_HOMESERVER=1/true is set).
/// False for normal Docker production and for `dotnet run` / `dotnet watch` from source.
/// </summary>
static bool IsRunningAsHomeserverHost()
{
    var flag = Environment.GetEnvironmentVariable("APP_HOMESERVER");
    if (string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(flag, "yes", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    try
    {
        var baseDir = NormalizeDir(AppContext.BaseDirectory);
        var appDir = NormalizeDir(HomeserverPaths.AppDirectory);
        if (baseDir.Equals(appDir, StringComparison.OrdinalIgnoreCase))
            return true;

        // Self-contained publish may nest under AppDirectory (e.g. extra subfolders).
        var prefix = appDir + Path.DirectorySeparatorChar;
        if (baseDir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return true;
    }
    catch
    {
        // Fall through — treat as non-homeserver.
    }

    return false;
}

static string ResolveContentRoot(bool homeserverHost)
{
    if (homeserverHost)
        return AppContext.BaseDirectory;

    // Prefer the project / working directory for interactive dev so wwwroot and
    // relative config match the repo layout. Fall back to BaseDirectory for
    // published non-homeserver hosts (Docker, plain publish).
    try
    {
        var cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "App.csproj"))
            || Directory.Exists(Path.Combine(cwd, "wwwroot")))
        {
            return cwd;
        }
    }
    catch
    {
        // ignore
    }

    return AppContext.BaseDirectory;
}

static string NormalizeDir(string path)
{
    var full = Path.GetFullPath(path);
    return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

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

    // "Data Source=data/homeserver.db" or "Data Source=./data/homeserver.db"
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

static string? FirstEnv(params string[] names)
{
    foreach (var name in names)
    {
        var value = Environment.GetEnvironmentVariable(name)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
    }

    return null;
}

