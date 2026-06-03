using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ChatfishApp.Data;
using ChatfishApp.Hubs;
using ChatfishApp.Components;
using ChatfishApp.Services;
using ChatfishApp.Services.Tools;
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

// Optional for future key encryption at rest (IDataProtector).
// builder.Services.AddDataProtection();

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


app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();


//app.MapHub<ChatHub>("/chathub").RequireAuthorization();

app.Run();

