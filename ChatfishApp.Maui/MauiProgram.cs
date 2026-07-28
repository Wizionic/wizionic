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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Velopack;
#if LINUX_DESKTOP
using ChatfishApp.Maui.Components;
using ChatfishApp.Maui.Services.Linux;
using WebKit.BlazorWebView.GirCore;
#endif

namespace ChatfishApp.Maui;

public static class MauiProgram
{
#if !LINUX_DESKTOP
	/// <summary>
	/// MAUI host entry (Windows / Android / iOS / Mac Catalyst).
	/// </summary>
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

		var configuration = BuildConfiguration();
		builder.Configuration.AddConfiguration(configuration);

		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		RegisterChatfishServices(builder.Services, configuration);

		builder.Services.AddMauiBlazorWebView();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();
		RestoreAuthCookies(app.Services);
		return app;
	}
#endif

#if LINUX_DESKTOP
	/// <summary>
	/// Linux (WebKit.BlazorWebView.GirCore) service provider: Chatfish services plus
	/// GirCore BlazorWebView options (root component, host page) and GLib dispatcher.
	/// </summary>
	public static IServiceProvider CreateLinuxServiceProvider()
	{
		VelopackApp.Build().Run();
		AppEnvironment.SetMaui();

		var configuration = BuildConfiguration();
		var services = new ServiceCollection();

		services.AddSingleton<IConfiguration>(configuration);
		services.AddLogging(lb =>
		{
			lb.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
#if DEBUG
			lb.SetMinimumLevel(LogLevel.Debug);
#else
			lb.SetMinimumLevel(LogLevel.Information);
#endif
		});

		RegisterChatfishServices(services, configuration);

		// WebKit.BlazorWebView.GirCore: registers BlazorWebViewOptions, GirCore dispatcher
		// (captures SynchronizationContext.Current — must be GLib main-loop context),
		// and Microsoft.AspNetCore.Components.WebView services.
		// Root component matches MainPage.xaml / wwwroot/index.html (#app).
		services.AddBlazorWebView(new BlazorWebViewOptions
		{
			RootComponent = typeof(Routes),
			HostPath = Path.Combine("wwwroot", "index.html")
		});

		var provider = services.BuildServiceProvider();
		RestoreAuthCookies(provider);
		return provider;
	}
#endif

	private static IConfiguration BuildConfiguration()
	{
		var configBuilder = new ConfigurationBuilder()
			.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

#if DEBUG
		configBuilder.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
#endif

		return configBuilder.Build();
	}

	private static void RegisterChatfishServices(IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<ChatfishServerOptions>(
			configuration.GetSection(ChatfishServerOptions.SectionName));

		services.AddSingleton<MauiAuthCookieStore>();
		services.AddSingleton<IAuthSessionPersistence>(sp => sp.GetRequiredService<MauiAuthCookieStore>());
		services.AddSingleton<HttpClient>(sp =>
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

		services.AddSingleton<ThemeService>();
		services.AddSingleton<SqliteSettingsDatabase>();
		services.AddSingleton<SqliteHistoryDatabase>();
		services.AddScoped<SqliteConversationStore>();
		services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<SqliteConversationStore>());
		services.AddScoped<SqliteNoteStore>();
		services.AddScoped<INoteStore>(sp => sp.GetRequiredService<SqliteNoteStore>());
		services.AddSingleton<ISyncPreferencesStore, SqliteSyncPreferencesStore>();
		services.AddSingleton<SipsorceryWebRtcTransport>();
		services.AddSingleton<IWebRtcTransport>(sp => sp.GetRequiredService<SipsorceryWebRtcTransport>());
		services.AddSingleton<MauiSyncService>();
		services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<MauiSyncService>());
		services.AddSingleton<INotesSyncBridge>(sp => sp.GetRequiredService<MauiSyncService>());
		services.AddSingleton<MauiSidebarState>();
		services.AddSingleton<ISidebarState>(sp => sp.GetRequiredService<MauiSidebarState>());
		services.AddSingleton<MauiBrowserPanelState>();
		services.AddSingleton<IBrowserPanelState>(sp => sp.GetRequiredService<MauiBrowserPanelState>());
		services.AddSingleton<MauiChatPanelState>();
		services.AddSingleton<IChatPanelState>(sp => sp.GetRequiredService<MauiChatPanelState>());
		services.AddSingleton<MauiNotesPanelState>();
		services.AddSingleton<INotesPanelState>(sp => sp.GetRequiredService<MauiNotesPanelState>());
		services.AddSingleton<NavLayoutService>();
		services.AddSingleton<INavLayoutState>(sp => sp.GetRequiredService<NavLayoutService>());
		services.AddSingleton<SqliteBrowserStore>();
		services.AddSingleton<IBrowserStore>(sp => sp.GetRequiredService<SqliteBrowserStore>());
		services.AddSingleton<SqliteBrowserSidebarStore>();
		services.AddSingleton<IBrowserSidebarStore>(sp => sp.GetRequiredService<SqliteBrowserSidebarStore>());
		services.AddSingleton<MauiBrowserSidePanelState>();
		services.AddSingleton<IBrowserSidePanelState>(sp => sp.GetRequiredService<MauiBrowserSidePanelState>());
		// Embedded browser downloads (toolbar + open/reveal/delete) — both desktop targets.
		services.AddSingleton<BrowserDownloadService>();
		services.AddSingleton<IBrowserDownloadService>(sp => sp.GetRequiredService<BrowserDownloadService>());
#if LINUX_DESKTOP
		// Native WebKit overlays (MainPage MAUI WebViews are not used on Linux).
		services.AddSingleton<LinuxBrowserOverlayService>();
		services.AddSingleton<IBrowserOverlaySync>(sp => sp.GetRequiredService<LinuxBrowserOverlayService>());
		services.AddSingleton<LinuxBrowserAgentService>();
		services.AddSingleton<IBrowserAgentService>(sp => sp.GetRequiredService<LinuxBrowserAgentService>());
		services.AddSingleton<IBrowserTabManager>(sp => sp.GetRequiredService<LinuxBrowserAgentService>());
		services.AddSingleton<LinuxSideBrowserService>();
		services.AddSingleton<IBrowserSideAgentService>(sp => sp.GetRequiredService<LinuxSideBrowserService>());
		services.AddSingleton<LinuxBrowserPlatformHooks>();
		services.AddSingleton<LinuxBrowserHost>();
#else
		services.AddSingleton<BrowserOverlayService>();
		services.AddSingleton<IBrowserOverlaySync>(sp => sp.GetRequiredService<BrowserOverlayService>());
		services.AddSingleton<MauiBrowserAgentService>();
		services.AddSingleton<IBrowserAgentService>(sp => sp.GetRequiredService<MauiBrowserAgentService>());
		services.AddSingleton<IBrowserTabManager>(sp => sp.GetRequiredService<MauiBrowserAgentService>());
		services.AddSingleton<MauiSideBrowserService>();
		services.AddSingleton<IBrowserSideAgentService>(sp => sp.GetRequiredService<MauiSideBrowserService>());
		services.AddSingleton<BrowserWebViewPlatformService>();
#endif
		services.AddSingleton<MauiPwaDetector>();
		services.AddSingleton<IPwaDetector>(sp => sp.GetRequiredService<MauiPwaDetector>());
		services.AddSingleton<SqliteKeyStore>();
		services.AddSingleton<IKeyStore>(sp => sp.GetRequiredService<SqliteKeyStore>());
		services.AddScoped<MauiCryptoService>();
		services.AddScoped<ICryptoService>(sp => sp.GetRequiredService<MauiCryptoService>());
		services.AddScoped<IGuestKeyProvider, SqliteGuestKeyProvider>();
		services.AddScoped<NullGuestDataMigrationService>();
		services.AddScoped<IGuestDataMigrationService>(sp => sp.GetRequiredService<NullGuestDataMigrationService>());
		services.AddSingleton<IToolExecutionTrace, ToolExecutionTrace>();
		services.AddSingleton<IRoutingSessionStore, InMemoryRoutingSessionStore>();
		services.AddSingleton<IRequestRouter, ContextualRequestRouter>();
		services.AddSingleton<ISmartHomeService, HomeAssistantService>();
		services.AddSingleton<MauiBrowserContext>();
		services.AddSingleton<IBrowserContext>(sp => sp.GetRequiredService<MauiBrowserContext>());
		services.AddSingleton<McpToolSource>();
		services.AddSingleton<IMcpToolRefresher>(sp => sp.GetRequiredService<McpToolSource>());
		services.AddSingleton<NativeToolModule>();
		services.AddSingleton<HomeAssistantToolModule>();
		services.AddSingleton<BrowserAgentToolModule>();
		services.AddSingleton<ChatfishApp.Shared.Services.Tools.LemonadeToolModule>();
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<NativeToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<HomeAssistantToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<BrowserAgentToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<ChatfishApp.Shared.Services.Tools.LemonadeToolModule>());
		services.AddSingleton<IToolProvider, CompositeToolProvider>();
		services.AddSingleton<ChatModelCatalogService>();
		services.AddSingleton<IChatModelCatalog>(sp => sp.GetRequiredService<ChatModelCatalogService>());
		services.AddSingleton<ChatCompletionService>();
		services.AddSingleton<IChatCompletionService>(sp => sp.GetRequiredService<ChatCompletionService>());
		services.AddSingleton<ChatfishApp.Core.Lemonade.ILemonadeImageService, ChatfishApp.Shared.Services.Lemonade.LemonadeImageService>();
		services.AddSingleton<ChatfishApp.Core.Lemonade.ILemonadeSpeechService, ChatfishApp.Shared.Services.Lemonade.LemonadeSpeechService>();
		services.AddSingleton<ChatAuthService>();
		services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<ChatAuthService>());
		services.AddHttpClient();
		services.AddSingleton<MauiUpdateService>();
		services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<MauiUpdateService>());
	}

	private static void RestoreAuthCookies(IServiceProvider services)
	{
		var cookieStore = services.GetRequiredService<MauiAuthCookieStore>();
		var serverOptions = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ChatfishServerOptions>>().Value;
		cookieStore.Configure(serverOptions);
		cookieStore.EnsureLoadedAsync().GetAwaiter().GetResult();
	}
}
