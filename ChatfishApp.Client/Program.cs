using ChatfishApp.Client.Services;
using ChatfishApp.Shared.Services.Mcp;
using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Chat;
using ChatfishApp.Core.Storage;
using ChatfishApp.Core.Sync;
using ChatfishApp.Core.UI;
using ChatfishApp.Shared.Services;
using ChatfishApp.Shared.Services.Tools;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<SidebarState>();
builder.Services.AddSingleton<ISidebarState>(sp => sp.GetRequiredService<SidebarState>());
builder.Services.AddSingleton<WasmKeyStore>();
builder.Services.AddSingleton<IKeyStore>(sp => sp.GetRequiredService<WasmKeyStore>());
builder.Services.AddScoped<ChatModelCatalogService>();
builder.Services.AddScoped<IChatModelCatalog>(sp => sp.GetRequiredService<ChatModelCatalogService>());
builder.Services.AddScoped<ChatCompletionService>();
builder.Services.AddScoped<IChatCompletionService>(sp => sp.GetRequiredService<ChatCompletionService>());
builder.Services.AddSingleton<JsSyncPreferencesStore>();
builder.Services.AddSingleton<ISyncPreferencesStore>(sp => sp.GetRequiredService<JsSyncPreferencesStore>());
builder.Services.AddScoped<JsWebRtcTransport>();
builder.Services.AddScoped<IWebRtcTransport>(sp => sp.GetRequiredService<JsWebRtcTransport>());
builder.Services.AddScoped<WasmSyncService>();
builder.Services.AddScoped<ISyncService>(sp => sp.GetRequiredService<WasmSyncService>());
builder.Services.AddScoped<INotesSyncBridge>(sp => sp.GetRequiredService<WasmSyncService>());
builder.Services.AddScoped<WasmConversationStore>();
builder.Services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<WasmConversationStore>());
builder.Services.AddScoped<WasmNoteStore>();
builder.Services.AddScoped<INoteStore>(sp => sp.GetRequiredService<WasmNoteStore>());
builder.Services.AddScoped<IGuestKeyProvider, BrowserGuestKeyProvider>();
builder.Services.AddScoped<ChatAuthService>();
builder.Services.AddScoped<IAuthService>(sp => sp.GetRequiredService<ChatAuthService>());
builder.Services.AddScoped<WasmCryptoService>();
builder.Services.AddScoped<ICryptoService>(sp => sp.GetRequiredService<WasmCryptoService>());
builder.Services.AddScoped<IGuestDataMigrationService, WasmGuestDataMigrationService>();
builder.Services.AddScoped<WasmGuestDataMigrationService>();

builder.Services.AddScoped<McpToolSource>();
builder.Services.AddScoped<IMcpToolRefresher>(sp => sp.GetRequiredService<McpToolSource>());
builder.Services.AddScoped<IToolProvider, DefaultToolProvider>();

var host = builder.Build();

var http = host.Services.GetRequiredService<HttpClient>();
AppTools.HttpClient = http;

var authService = host.Services.GetRequiredService<IAuthService>();
var guestMigration = host.Services.GetRequiredService<IGuestDataMigrationService>();
var syncService = host.Services.GetRequiredService<ISyncService>();
await authService.LoadAsync();
await guestMigration.MigrateIfNeededAsync();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ChatModelCatalogService>().RefreshAsync();
}
await syncService.InitializeAsync();

if (authService.IsAuthenticated)
{
    await syncService.EnsureConnectedAndRegisteredAsync();
    if (!string.IsNullOrEmpty(syncService.AiServerDeviceId))
        await syncService.EnsureAiProxyConnectionAsync();
}

await host.RunAsync();