using ChatfishApp.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<ChatfishApp.Client.Services.SidebarState>();
builder.Services.AddSingleton<ChatfishApp.Client.Services.WasmKeyStore>();
builder.Services.AddSingleton<ChatfishApp.Client.Services.WasmAiProviderService>();
builder.Services.AddScoped<ChatfishApp.Client.Services.WasmChatCompletionService>();
// Live device presence + (future) sync signaling over SignalR.
// Must be Scoped because it depends on HttpClient (Scoped) and WasmAuthService (Scoped).
// In Blazor WASM there is effectively one scope for the lifetime of the app, so this is
// still long-lived and appropriate for holding the SignalR HubConnection.
// Using Singleton would cause a ScopedInSingletonException during service validation at startup.
builder.Services.AddScoped<ChatfishApp.Client.Services.WasmSyncService>();

// These must be Scoped because they depend on HttpClient (which is Scoped in Blazor WASM).
// Using Scoped here is fine and common in WASM apps — they live for the lifetime of the WASM app.
builder.Services.AddScoped<ChatfishApp.Client.Services.WasmConversationStore>();
builder.Services.AddScoped<ChatfishApp.Client.Services.WasmNoteStore>();
builder.Services.AddScoped<ChatfishApp.Client.Services.WasmAuthService>();
builder.Services.AddScoped<ChatfishApp.Client.Services.WasmCryptoService>();

// App-level tools for agentic / tool-calling support in WASM (same tools as server, executed in browser).
// DefaultToolProvider now also pulls in remote MCP tools selected by the user on the Tools page.
builder.Services.AddSingleton<ChatfishApp.Client.Services.Mcp.McpToolSource>();
builder.Services.AddSingleton<ChatfishApp.Services.Tools.IToolProvider, ChatfishApp.Services.Tools.DefaultToolProvider>();

// Configure static HttpClients for same-origin server proxies (tools + AI providers).
// Setting BaseAddress ensures relative calls like "/api/tools/..." and "/api/proxy/..." resolve
// to the host server (important for the proxies to work reliably from the browser).
var serverProxyHttp = new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    Timeout = TimeSpan.FromMinutes(10)
};
ChatfishApp.Services.Tools.AppTools.HttpClient = serverProxyHttp;
ChatfishApp.Client.Services.WasmAiProviderService.ProxyHttp = serverProxyHttp;

var host = builder.Build();

// Eagerly resolve WasmSyncService (and its dependencies) at startup so that
// it connects to the signaling hub and can receive incoming WebRTC sync
// payloads even if the user never opens the /sync page.
// As long as any WASM page is loaded and the user is authenticated, syncs
// will be received in the background and persisted automatically.
var authService = host.Services.GetRequiredService<WasmAuthService>();
var syncService = host.Services.GetRequiredService<WasmSyncService>();
var aiProvider = host.Services.GetRequiredService<WasmAiProviderService>();

await authService.LoadAsync();
await aiProvider.RefreshProxiedProvidersAsync();
await syncService.InitializeAsync();

if (authService.IsAuthenticated)
{
    await syncService.EnsureConnectedAndRegisteredAsync();
    if (!string.IsNullOrEmpty(syncService.AiServerDeviceId))
        await syncService.EnsureAiProxyConnectionAsync();
}

await host.RunAsync();
