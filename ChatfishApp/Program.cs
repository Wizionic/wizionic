using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ChatfishApp.Data;
using ChatfishApp.Hubs;
using ChatfishApp.Components;
using ChatfishApp.Services;
using ChatfishApp.Services.Tools;
using ChatfishApp.Apis;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddDbContext<ChatfishDbContext>(options =>
{
    options.UseSqlite("Data Source=chatfish.db");
});

// NOTE: Removed the previous hardcoded Groq HttpClient + embedded API key (security issue).
// Keys are now per-user via ProviderKeyService + AiProviderService (using IChatClient abstraction).

builder.Services.AddScoped<MagicLinkService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<ProviderKeyService>();
builder.Services.AddScoped<AiProviderService>();

// App-level tools (web search, URL summarization via Jina, etc.) exposed to models via ME.AI function calling.
// These are shared (not per-user) and let capable models act agentically ("search the web when it needs to").
builder.Services.AddSingleton<IToolProvider, DefaultToolProvider>();

// Shared state for WASM sidebar toggle (used by WasmTopBar in WasmLayout for /wasm-chat etc.)
// Must be registered here (main app DI) so that server-side rendering of WASM pages (layout + topbar)
// can provide the service. The Client's DI also registers it for the interactive WASM runtime.
builder.Services.AddSingleton<ChatfishApp.Client.Services.SidebarState>();

// HttpClient for WASM client components (e.g. WasmSettings) during any server-side rendering of the component tree (layout, topbar, etc.).
// The actual interactive WASM runtime (in Client/Program.cs) provides its own configured instance (with BaseAddress).
builder.Services.AddScoped<HttpClient>();

// Data Protection for at-rest encryption of sensitive per-user values (e.g. the LocalEncryptionKey
// used by WASM clients for browser-stored history blobs + live sync payloads, and the existing
// UserProviderKey.Key values). The protector is used server-side when storing/retrieving these
// values; authenticated WASM clients receive the *unprotected* key over TLS+cookie so they can
// perform client-side AES-GCM encryption of their local data and of blobs transferred during
// live (both-devices-open) sync. The server never stores the WASM history content itself.
// See User.LocalEncryptionKey and the Wasm*Store + WasmLiveSyncClient in the Client project.
builder.Services.AddDataProtection();
builder.Services.AddSingleton<KeyProtectionService>();

builder.Services.AddAuthentication("ChatfishAuth")
    .AddCookie("ChatfishAuth", options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();


var app = builder.Build();

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
        ctx.Response.Redirect("/login");
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

    ctx.Response.Redirect("/chat");
});


app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("ChatfishAuth");
    ctx.Response.Redirect("/login");
});

// WASM client APIs (kept in WasmApiEndpoints.cs so Program.cs stays small).
// All under /api + cookie auth. See Apis/WasmApiEndpoints.cs for the implementations.
app.MapWasmApis();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ChatfishApp.Client.WasmMarker).Assembly);


//app.MapHub<ChatHub>("/chathub").RequireAuthorization();

app.Run();

