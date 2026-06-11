using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ChatfishApp.Data;
using ChatfishApp.Components;
using ChatfishApp.Services;
using ChatfishApp.Services.Tools;
using ChatfishApp.Apis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddDbContext<ChatfishDbContext>(options =>
{
    options.UseSqlite("Data Source=chatfish.db");
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

// App-level tools (web search, URL summarization via Jina, etc.) exposed to models via ME.AI function calling.
// These are shared (not per-user) and let capable models act agentically ("search the web when it needs to").
builder.Services.AddSingleton<IToolProvider, DefaultToolProvider>();

// Shared state for WASM sidebar toggle (used by WasmTopBar in WasmLayout for /chat etc.)
// Must be registered here (main app DI) so that server-side rendering of WASM pages (layout + topbar)
// can provide the service. The Client's DI also registers it for the interactive WASM runtime.
builder.Services.AddSingleton<ChatfishApp.Client.Services.SidebarState>();

// HttpClient for WASM client components (e.g. Settings) during any server-side rendering of the component tree (layout, topbar, etc.).
// The actual interactive WASM runtime (in Client/Program.cs) provides its own configured instance (with BaseAddress).
// Keep this registration minimal — do not register client-only services like WasmAuthService or WasmSyncService here,
// otherwise they can interfere with WebAssembly bootstrap, render mode activation, and hot reload under `dotnet watch`.
builder.Services.AddScoped<HttpClient>();

// Data Protection for at-rest encryption of sensitive per-user values (e.g. the LocalEncryptionKey
// used by WASM clients for browser-stored history blobs + live sync payloads, and the existing
// UserProviderKey.Key values). The protector is used server-side when storing/retrieving these
// values; authenticated WASM clients receive the *unprotected* key over TLS+cookie so they can
// perform client-side AES-GCM encryption of their local data and of blobs transferred during
// live (both-devices-open) sync. The server never stores the WASM history content itself.
// See User.LocalEncryptionKey and the Wasm*Store + WasmLiveSyncClient in the Client project.
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

builder.Services.AddAuthentication("ChatfishAuth")
    .AddCookie("ChatfishAuth", options =>
    {
        // Root (/) is now the WASM landing page that handles both "login with email" and
        // "continue without login" (guest/local mode). The cookie middleware therefore
        // redirects unauthenticated users who hit a [Authorize] page to "/".
        options.LoginPath = "/";
        options.LogoutPath = "/logout";

        // Long-lived sessions (user request: stay logged in >=30 days, prefer longer).
        // 90 days with sliding renewal. Persisted DP keys (above) prevent invalidation on restarts/sleeps.
        options.ExpireTimeSpan = TimeSpan.FromDays(90);
        options.SlidingExpiration = true;

        // Production hardening: always require Secure (https), and Lax SameSite so magic-link
        // email clicks (cross-site top-level navigation) still work while protecting against CSRF on APIs.
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
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

app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatfishDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/magic-login", async (HttpContext ctx, string token, MagicLinkService magicLinks) =>
{
    var user = await magicLinks.ValidateMagicLinkAsync(token);

    if (user == null)
    {
        // Invalid/expired token → send back to the new WASM root landing page (guest form).
        ctx.Response.Redirect("/");
        return;
    }

    var claims = new List<Claim>
    {
        new Claim(ClaimTypes.Name, user.Email),
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
    };

    var identity = new ClaimsIdentity(claims, "ChatfishAuth");
    var principal = new ClaimsPrincipal(identity);

    await ctx.SignInAsync("ChatfishAuth", principal);

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

