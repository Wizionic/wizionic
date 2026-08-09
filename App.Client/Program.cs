using App.Client.Services;
using App.Shared.Services.Mcp;
using App.Core.Auth;
using App.Core.Chat;
using App.Core.Storage;
using App.Core.Sync;
using App.Core.UI;
using App.Core.Update;
using App.Shared.Services;
using App.Core.Browser;
using App.Core.SmartHome;
using App.Core.Tools;
using App.Shared.Services.Tools;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Singleton HttpClient + auth so keystore/settings can share one multi-user storage prefix.
builder.Services.AddSingleton(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddSingleton<SidebarState>();
builder.Services.AddSingleton<ISidebarState>(sp => sp.GetRequiredService<SidebarState>());
builder.Services.AddSingleton<BrowserPanelState>();
builder.Services.AddSingleton<IBrowserPanelState>(sp => sp.GetRequiredService<BrowserPanelState>());
builder.Services.AddSingleton<ChatPanelState>();
builder.Services.AddSingleton<IChatPanelState>(sp => sp.GetRequiredService<ChatPanelState>());
builder.Services.AddSingleton<NotesPanelState>();
builder.Services.AddSingleton<INotesPanelState>(sp => sp.GetRequiredService<NotesPanelState>());
builder.Services.AddSingleton<NavLayoutService>();
builder.Services.AddSingleton<INavLayoutState>(sp => sp.GetRequiredService<NavLayoutService>());
builder.Services.AddSingleton<IBrowserAgentService, NullBrowserAgentService>();
builder.Services.AddSingleton<IBrowserTabManager, NullBrowserTabManager>();
builder.Services.AddSingleton<IBrowserOverlaySync, NullBrowserOverlaySync>();
builder.Services.AddSingleton<IBrowserStore, NullBrowserStore>();
builder.Services.AddSingleton<IBrowserSidebarStore, NullBrowserSidebarStore>();
builder.Services.AddSingleton<BrowserSidePanelState>();
builder.Services.AddSingleton<IBrowserSidePanelState>(sp => sp.GetRequiredService<BrowserSidePanelState>());
builder.Services.AddSingleton<IBrowserSideAgentService, NullBrowserSideAgentService>();
builder.Services.AddSingleton<IPwaDetector, NullPwaDetector>();
builder.Services.AddSingleton<IBrowserDownloadService, NullBrowserDownloadService>();
// Singleton so settings survive navigation and stay aligned with auth + multi-user prefixes.
builder.Services.AddSingleton<WasmKeyStore>();
builder.Services.AddSingleton<IKeyStore>(sp => sp.GetRequiredService<WasmKeyStore>());
builder.Services.AddScoped<ChatModelCatalogService>();
builder.Services.AddScoped<IChatModelCatalog>(sp => sp.GetRequiredService<ChatModelCatalogService>());
builder.Services.AddScoped<ChatCompletionService>();
builder.Services.AddScoped<IChatCompletionService>(sp => sp.GetRequiredService<ChatCompletionService>());
builder.Services.AddScoped<App.Core.Lemonade.ILemonadeImageService, App.Shared.Services.Lemonade.LemonadeImageService>();
builder.Services.AddScoped<App.Core.Lemonade.ILemonadeSpeechService, App.Shared.Services.Lemonade.LemonadeSpeechService>();
builder.Services.AddSingleton<JsSyncPreferencesStore>();
builder.Services.AddSingleton<ISyncPreferencesStore>(sp => sp.GetRequiredService<JsSyncPreferencesStore>());
builder.Services.AddSingleton<SettingsSyncStore>();
builder.Services.AddSingleton<ISettingsSyncStore>(sp => sp.GetRequiredService<SettingsSyncStore>());
builder.Services.AddScoped<JsWebRtcTransport>();
builder.Services.AddScoped<IWebRtcTransport>(sp => sp.GetRequiredService<JsWebRtcTransport>());
builder.Services.AddScoped<WasmSyncService>();
builder.Services.AddScoped<ISyncService>(sp => sp.GetRequiredService<WasmSyncService>());
builder.Services.AddScoped<INotesSyncBridge>(sp => sp.GetRequiredService<WasmSyncService>());
builder.Services.AddScoped<WasmConversationStore>();
builder.Services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<WasmConversationStore>());
builder.Services.AddScoped<WasmNoteStore>();
builder.Services.AddScoped<INoteStore>(sp => sp.GetRequiredService<WasmNoteStore>());
builder.Services.AddScoped<WasmGalleryStore>();
builder.Services.AddScoped<IGalleryStore>(sp => sp.GetRequiredService<WasmGalleryStore>());
builder.Services.AddScoped<IGallerySyncBridge>(sp => sp.GetRequiredService<WasmSyncService>());
builder.Services.AddScoped<WasmCalendarStore>();
builder.Services.AddScoped<ICalendarStore>(sp => sp.GetRequiredService<WasmCalendarStore>());
builder.Services.AddScoped<ICalendarSyncBridge>(sp => sp.GetRequiredService<WasmSyncService>());
builder.Services.AddScoped<IChatMediaLibrary, App.Shared.Services.ChatMediaLibrary>();
builder.Services.AddSingleton<App.Core.Storage.IGalleryChatHandoff, App.Shared.Services.GalleryChatHandoff>();
builder.Services.AddScoped<WasmStorageQuotaService>();
builder.Services.AddScoped<IStorageQuotaService>(sp => sp.GetRequiredService<WasmStorageQuotaService>());
// Guest key provider must be Singleton: ChatAuthService is Singleton and injects IGuestKeyProvider.
builder.Services.AddSingleton<IGuestKeyProvider, BrowserGuestKeyProvider>();
// Singleton auth so all stores share one identity/prefix (multi-user isolation).
builder.Services.AddSingleton<ChatAuthService>();
builder.Services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<ChatAuthService>());
builder.Services.AddScoped<WasmCryptoService>();
builder.Services.AddScoped<ICryptoService>(sp => sp.GetRequiredService<WasmCryptoService>());
builder.Services.AddScoped<IGuestDataMigrationService, WasmGuestDataMigrationService>();
builder.Services.AddScoped<WasmGuestDataMigrationService>();
builder.Services.AddSingleton<IUpdateService>(sp => NullUpdateService.Instance);
builder.Services.AddSingleton<App.Core.Homeserver.IHomeserverInstallService>(
    _ => App.Shared.Services.NullHomeserverInstallService.Instance);
builder.Services.AddSingleton<App.Core.Configuration.IAppServerEndpoint>(
    _ => App.Shared.Services.NullAppServerEndpoint.Instance);
builder.Services.AddSingleton<App.Core.Setup.ISetupWizardHost>(
    _ => App.Shared.Services.NullSetupWizardHost.Instance);
builder.Services.AddSingleton<App.Core.Lemonade.ILemonadeInstallService>(
    _ => App.Shared.Services.NullLemonadeInstallService.Instance);
builder.Services.AddSingleton<App.Core.Ollama.IOllamaInstallService>(
    _ => App.Shared.Services.NullOllamaInstallService.Instance);
builder.Services.AddSingleton<App.Core.UI.IUrlEmbedOverlay>(
    _ => App.Shared.Services.NullUrlEmbedOverlay.Instance);

// Scoped with ChatCompletionService so each completion owns its tool trace
// (ToolExecutionTrace no longer uses AsyncLocal — that type fails on WASM).
builder.Services.AddScoped<IToolExecutionTrace, ToolExecutionTrace>();
builder.Services.AddSingleton<IRoutingSessionStore, InMemoryRoutingSessionStore>();
builder.Services.AddScoped<ContextualRequestRouter>();
builder.Services.AddScoped<AiRequestRouter>();
builder.Services.AddScoped<IRequestRouter, CompositeRequestRouter>();
builder.Services.AddSingleton<ISmartHomeService, NullSmartHomeService>();
builder.Services.AddSingleton<IBrowserContext, NullBrowserContext>();
builder.Services.AddScoped<McpToolSource>();
builder.Services.AddScoped<IMcpToolRefresher>(sp => sp.GetRequiredService<McpToolSource>());
builder.Services.AddSingleton<App.Core.Tools.IConversationMediaBuffer, App.Shared.Services.Tools.ConversationMediaBuffer>();
builder.Services.AddScoped<App.Core.Tools.IToolConversationContext, App.Shared.Services.Tools.ToolConversationContext>();
builder.Services.AddScoped<NativeToolModule>();
builder.Services.AddScoped<App.Shared.Services.Tools.LemonadeToolModule>();
builder.Services.AddScoped<App.Shared.Services.Tools.GalleryToolModule>();
builder.Services.AddScoped<App.Shared.Services.Tools.CalendarToolModule>();
builder.Services.AddScoped<App.Shared.Services.Tools.NotesToolModule>();
builder.Services.AddScoped<IToolModule>(sp => sp.GetRequiredService<NativeToolModule>());
builder.Services.AddScoped<IToolModule>(sp => sp.GetRequiredService<App.Shared.Services.Tools.LemonadeToolModule>());
builder.Services.AddScoped<IToolModule>(sp => sp.GetRequiredService<App.Shared.Services.Tools.GalleryToolModule>());
builder.Services.AddScoped<IToolModule>(sp => sp.GetRequiredService<App.Shared.Services.Tools.CalendarToolModule>());
builder.Services.AddScoped<IToolModule>(sp => sp.GetRequiredService<App.Shared.Services.Tools.NotesToolModule>());
builder.Services.AddScoped<IToolProvider, CompositeToolProvider>();

var host = builder.Build();

var authService = host.Services.GetRequiredService<IAuthService>();
var guestMigration = host.Services.GetRequiredService<IGuestDataMigrationService>();
var syncService = host.Services.GetRequiredService<ISyncService>();
var keyStore = host.Services.GetRequiredService<IKeyStore>();
await authService.LoadAsync();
await keyStore.LoadAsync();
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