using System.Net;
using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Browser;
using ChatfishApp.Core.Configuration;
using ChatfishApp.Core.SmartHome;
using ChatfishApp.Core.Storage;
using ChatfishApp.Core.Sync;
using ChatfishApp.Core.Chat;
using ChatfishApp.Core.Tools;
using ChatfishApp.Core.UI;
using ChatfishApp.Core.Update;
using ChatfishApp.Maui.Services;
using ChatfishApp.Shared.Services;
using ChatfishApp.Shared.Services.Mcp;
using ChatfishApp.Shared.Services.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Velopack;

namespace ChatfishApp.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
#if WINDOWS
		var userDataFolder = Path.Combine(FileSystem.AppDataDirectory, "WebView2");
		Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
#endif
		// Must be the very first line before anything else
        VelopackApp.Build().Run();

		AppEnvironment.SetMaui();
		
		var builder = MauiApp.CreateBuilder();

		var configBuilder = new ConfigurationBuilder()
			.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

#if DEBUG
		configBuilder.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
#endif

		var configuration = configBuilder.Build();
		builder.Configuration.AddConfiguration(configuration);

		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.Configure<ChatfishServerOptions>(
			builder.Configuration.GetSection(ChatfishServerOptions.SectionName));

		builder.Services.AddSingleton<MauiAuthCookieStore>();
		builder.Services.AddSingleton<IAuthSessionPersistence>(sp => sp.GetRequiredService<MauiAuthCookieStore>());
		builder.Services.AddSingleton<HttpClient>(sp =>
		{
			var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ChatfishServerOptions>>().Value;
			var store = sp.GetRequiredService<MauiAuthCookieStore>();
			store.Configure(options);
			var inner = new HttpClientHandler
			{
				CookieContainer = store.Container,
				UseCookies = true,
				AutomaticDecompression = DecompressionMethods.All
			};
			var handler = new PersistCookiesHandler(store)
			{
				InnerHandler = inner
			};
			return new HttpClient(handler)
			{
				BaseAddress = options.BaseUri,
				Timeout = TimeSpan.FromSeconds(60)
			};
		});

		builder.Services.AddSingleton<ThemeService>();
		builder.Services.AddSingleton<SqliteSettingsDatabase>();
		builder.Services.AddSingleton<SqliteHistoryDatabase>();
		builder.Services.AddScoped<SqliteConversationStore>();
		builder.Services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<SqliteConversationStore>());
		builder.Services.AddScoped<SqliteNoteStore>();
		builder.Services.AddScoped<INoteStore>(sp => sp.GetRequiredService<SqliteNoteStore>());
		builder.Services.AddSingleton<ISyncPreferencesStore, SqliteSyncPreferencesStore>();
		builder.Services.AddSingleton<SipsorceryWebRtcTransport>();
		builder.Services.AddSingleton<IWebRtcTransport>(sp => sp.GetRequiredService<SipsorceryWebRtcTransport>());
		builder.Services.AddSingleton<MauiSyncService>();
		builder.Services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<MauiSyncService>());
		builder.Services.AddSingleton<INotesSyncBridge>(sp => sp.GetRequiredService<MauiSyncService>());
		builder.Services.AddSingleton<MauiSidebarState>();
		builder.Services.AddSingleton<ISidebarState>(sp => sp.GetRequiredService<MauiSidebarState>());
		builder.Services.AddSingleton<MauiBrowserPanelState>();
		builder.Services.AddSingleton<IBrowserPanelState>(sp => sp.GetRequiredService<MauiBrowserPanelState>());
		builder.Services.AddSingleton<SqliteBrowserStore>();
		builder.Services.AddSingleton<IBrowserStore>(sp => sp.GetRequiredService<SqliteBrowserStore>());
		builder.Services.AddSingleton<SqliteBrowserSidebarStore>();
		builder.Services.AddSingleton<IBrowserSidebarStore>(sp => sp.GetRequiredService<SqliteBrowserSidebarStore>());
		builder.Services.AddSingleton<MauiBrowserSidePanelState>();
		builder.Services.AddSingleton<IBrowserSidePanelState>(sp => sp.GetRequiredService<MauiBrowserSidePanelState>());
		builder.Services.AddSingleton<BrowserOverlayService>();
		builder.Services.AddSingleton<IBrowserOverlaySync>(sp => sp.GetRequiredService<BrowserOverlayService>());
		builder.Services.AddSingleton<MauiBrowserAgentService>();
		builder.Services.AddSingleton<IBrowserAgentService>(sp => sp.GetRequiredService<MauiBrowserAgentService>());
		builder.Services.AddSingleton<MauiSideBrowserService>();
		builder.Services.AddSingleton<IBrowserSideAgentService>(sp => sp.GetRequiredService<MauiSideBrowserService>());
		builder.Services.AddSingleton<MauiPwaDetector>();
		builder.Services.AddSingleton<IPwaDetector>(sp => sp.GetRequiredService<MauiPwaDetector>());
		builder.Services.AddSingleton<BrowserWebViewPlatformService>();
		builder.Services.AddSingleton<SqliteKeyStore>();
		builder.Services.AddSingleton<IKeyStore>(sp => sp.GetRequiredService<SqliteKeyStore>());
		builder.Services.AddScoped<MauiCryptoService>();
		builder.Services.AddScoped<ICryptoService>(sp => sp.GetRequiredService<MauiCryptoService>());
		builder.Services.AddScoped<IGuestKeyProvider, SqliteGuestKeyProvider>();
		builder.Services.AddScoped<NullGuestDataMigrationService>();
		builder.Services.AddScoped<IGuestDataMigrationService>(sp => sp.GetRequiredService<NullGuestDataMigrationService>());
		builder.Services.AddSingleton<IToolExecutionTrace, ToolExecutionTrace>();
		builder.Services.AddSingleton<IRoutingSessionStore, InMemoryRoutingSessionStore>();
		builder.Services.AddSingleton<IRequestRouter, ContextualRequestRouter>();
		builder.Services.AddSingleton<ISmartHomeService, HomeAssistantService>();
		builder.Services.AddSingleton<MauiBrowserContext>();
		builder.Services.AddSingleton<IBrowserContext>(sp => sp.GetRequiredService<MauiBrowserContext>());
		builder.Services.AddSingleton<McpToolSource>();
		builder.Services.AddSingleton<IMcpToolRefresher>(sp => sp.GetRequiredService<McpToolSource>());
		builder.Services.AddSingleton<NativeToolModule>();
		builder.Services.AddSingleton<HomeAssistantToolModule>();
		builder.Services.AddSingleton<BrowserAgentToolModule>();
		builder.Services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<NativeToolModule>());
		builder.Services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<HomeAssistantToolModule>());
		builder.Services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<BrowserAgentToolModule>());
		builder.Services.AddSingleton<IToolProvider, CompositeToolProvider>();
		builder.Services.AddSingleton<ChatModelCatalogService>();
		builder.Services.AddSingleton<IChatModelCatalog>(sp => sp.GetRequiredService<ChatModelCatalogService>());
		builder.Services.AddSingleton<ChatCompletionService>();
		builder.Services.AddSingleton<IChatCompletionService>(sp => sp.GetRequiredService<ChatCompletionService>());
		builder.Services.AddSingleton<ChatAuthService>();
		builder.Services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<ChatAuthService>());
		builder.Services.AddSingleton<MauiUpdateService>();
		builder.Services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<MauiUpdateService>());

		builder.Services.AddMauiBlazorWebView();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		// Restore persisted auth cookies before Blazor components call LoadAsync / connect SignalR.
		var cookieStore = app.Services.GetRequiredService<MauiAuthCookieStore>();
		var serverOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ChatfishServerOptions>>().Value;
		cookieStore.Configure(serverOptions);
		cookieStore.EnsureLoadedAsync().GetAwaiter().GetResult();

		return app;
	}
}