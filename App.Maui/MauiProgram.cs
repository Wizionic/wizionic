using System.Net;
using App.Core.Auth;
using App.Core.Browser;
using App.Core.Configuration;
using App.Core.Homeserver;
using App.Core.Lemonade;
using App.Core.Ollama;
using App.Core.Setup;
using App.Core.SmartHome;
using App.Core.Storage;
using App.Core.Sync;
using App.Core.Chat;
using App.Core.Connectors;
using App.Core.Tools;
using App.Core.UI;
using App.Core.Update;
using App.Maui.Services;
using App.Shared.Services;
using App.Shared.Services.Mcp;
using App.Shared.Services.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Velopack;
#if LINUX_DESKTOP
using App.Maui.Components;
using App.Maui.Services.Linux;
using WebKit.BlazorWebView.GirCore;
#endif

namespace App.Maui;

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
		// Must be the very first line before anything else.
		// FastCallbacks must exit quickly and must not show UI.
		var firstRun = false;
		var afterUpdate = false;
		VelopackApp.Build()
			.OnFirstRun(_ => firstRun = true)
			.OnAfterUpdateFastCallback(_ =>
			{
				afterUpdate = true;
				try
				{
					Directory.CreateDirectory(HomeserverPaths.RootDirectory);
					File.WriteAllText(HomeserverPaths.PendingUpdateFlagPath, DateTimeOffset.UtcNow.ToString("O"));
				}
				catch
				{
					// best effort — MainPage will still try UpdateIfNeeded when installed
				}
			})
			.OnBeforeUninstallFastCallback(_ =>
			{
				try
				{
					// Stop homeserver service before MAUI uninstall; leave SQLite data intact.
					if (OperatingSystem.IsWindows())
					{
						var psi = new System.Diagnostics.ProcessStartInfo
						{
							FileName = "sc.exe",
							Arguments = $"stop {HomeserverPaths.ServiceName}",
							UseShellExecute = false,
							CreateNoWindow = true
						};
						System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);
#if WINDOWS
						WindowsStartupRegistration.Delete();
						WindowsSingleInstance.RequestQuit();
#endif
					}
				}
				catch
				{
					// ignore — uninstall continues
				}
			})
			.Run();

#if WINDOWS
		if (!WindowsSingleInstance.TryAcquirePrimary())
		{
			WindowsSingleInstance.RequestShow();
			Environment.Exit(0);
		}
#endif

		AppEnvironment.SetMaui();

		var builder = MauiApp.CreateBuilder();

		var configuration = BuildConfiguration();
		builder.Configuration.AddConfiguration(configuration);

		// Stash Velopack lifecycle flags for the DI-backed homeserver service after build.
		builder.Services.AddSingleton(new HomeserverLaunchFlags(firstRun, afterUpdate));
		// First Velopack run or incomplete onboarding → auto-show setup wizard.
		builder.Services.AddSingleton<ISetupWizardHost>(sp =>
			new MauiSetupWizardHost(autoShowOnFirstRun: firstRun));

		builder
			.UseMauiApp<MauiShell>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		RegisterAppServices(builder.Services, configuration);

		builder.Services.AddMauiBlazorWebView();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();
		RestoreAuthCookies(app.Services);
		app.Services.GetRequiredService<WorkflowDueHost>().Start();
		_ = StartMauiSyncAsync(app.Services);
		// Warm OAuth interceptor so in-app browser navigations are watched before first Tools click.
		_ = app.Services.GetService<MauiOAuthInterceptor>();
		return app;
	}
#endif

#if LINUX_DESKTOP
	/// <summary>
	/// Linux (WebKit.BlazorWebView.GirCore) service provider: Wizionic services plus
	/// GirCore BlazorWebView options (root component, host page) and GLib dispatcher.
	/// </summary>
	public static IServiceProvider CreateLinuxServiceProvider()
	{
		// Capture Velopack lifecycle for homeserver update flags (same idea as Windows).
		var firstRun = false;
		var afterUpdate = false;
		VelopackApp.Build()
			.OnFirstRun(_ => firstRun = true)
			.OnAfterUpdateFastCallback(_ =>
			{
				afterUpdate = true;
				try
				{
					Directory.CreateDirectory(HomeserverPaths.RootDirectory);
					File.WriteAllText(HomeserverPaths.PendingUpdateFlagPath, DateTimeOffset.UtcNow.ToString("O"));
				}
				catch
				{
					// ignore — update continues
				}
			})
			.OnBeforeUninstallFastCallback(_ =>
			{
				try { LinuxAutostartRegistration.Delete(); }
				catch { /* uninstall continues */ }
			})
			.Run();
		AppEnvironment.SetMaui();

		var configuration = BuildConfiguration();
		var services = new ServiceCollection();

		services.AddSingleton(new HomeserverLaunchFlags(firstRun, afterUpdate));
		services.AddSingleton<ISetupWizardHost>(sp =>
			new MauiSetupWizardHost(autoShowOnFirstRun: firstRun));

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

		RegisterAppServices(services, configuration);

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
		provider.GetRequiredService<WorkflowDueHost>().Start();
		_ = StartMauiSyncAsync(provider);
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

		// Written when the user opts into a local Home Server (retargets BaseUrl, keeps update feed).
		var localOverride = Path.Combine(MauiAppData.Directory, "appsettings.Local.json");
		if (File.Exists(localOverride))
			configBuilder.AddJsonFile(localOverride, optional: true, reloadOnChange: false);

		return configBuilder.Build();
	}

	private static void RegisterAppServices(IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<AppServerOptions>(
			configuration.GetSection(AppServerOptions.SectionName));

		services.AddSingleton<MauiAuthCookieStore>();
		services.AddSingleton<IAuthSessionPersistence>(sp => sp.GetRequiredService<MauiAuthCookieStore>());
		services.AddSingleton<HttpClient>(sp =>
		{
			var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppServerOptions>>().Value;
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
		services.AddSingleton<MauiAppServerEndpoint>();
		services.AddSingleton<IAppServerEndpoint>(sp => sp.GetRequiredService<MauiAppServerEndpoint>());
		services.AddSingleton<MauiAppRestartService>();
		services.AddSingleton<IAppRestartService>(sp => sp.GetRequiredService<MauiAppRestartService>());
#if WINDOWS
		services.AddSingleton<WindowsDesktopHost>();
		services.AddSingleton<IDesktopShellService>(sp => sp.GetRequiredService<WindowsDesktopHost>());
#elif LINUX_DESKTOP
		services.AddSingleton<LinuxDesktopHost>();
		services.AddSingleton<IDesktopShellService>(sp => sp.GetRequiredService<LinuxDesktopHost>());
#else
		services.AddSingleton<IDesktopShellService>(_ => NullDesktopShellService.Instance);
#endif

		services.AddSingleton<ThemeService>();
		services.AddSingleton<SqliteSettingsDatabase>();
		services.AddSingleton<SqliteHistoryDatabase>();
		services.AddScoped<SqliteConversationStore>();
		services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<SqliteConversationStore>());
		services.AddScoped<SqliteNoteStore>();
		services.AddScoped<INoteStore>(sp => sp.GetRequiredService<SqliteNoteStore>());
		services.AddScoped<SqliteGalleryStore>();
		services.AddScoped<IGalleryStore>(sp => sp.GetRequiredService<SqliteGalleryStore>());
		services.AddScoped<SqliteCalendarStore>();
		services.AddScoped<ICalendarStore>(sp => sp.GetRequiredService<SqliteCalendarStore>());
		services.AddScoped<IChatMediaLibrary, App.Shared.Services.ChatMediaLibrary>();
		services.AddSingleton<App.Core.Storage.IGalleryChatHandoff, App.Shared.Services.GalleryChatHandoff>();
		services.AddSingleton<App.Core.Storage.INotesChatHandoff, App.Shared.Services.NotesChatHandoff>();
		services.AddSingleton<MauiStorageQuotaService>();
		services.AddSingleton<IStorageQuotaService>(sp => sp.GetRequiredService<MauiStorageQuotaService>());
		services.AddSingleton<ISyncPreferencesStore, SqliteSyncPreferencesStore>();
		services.AddSingleton<App.Shared.Services.Help.HelpCatalogService>(_ => new App.Shared.Services.Help.HelpCatalogService());
		services.AddSingleton<App.Core.Help.IHelpCatalog>(sp => sp.GetRequiredService<App.Shared.Services.Help.HelpCatalogService>());
		services.AddSingleton<App.Shared.Services.Help.HelpOverlay>();
		services.AddSingleton<App.Core.Help.IHelpOverlay>(sp => sp.GetRequiredService<App.Shared.Services.Help.HelpOverlay>());
		services.AddSingleton<App.Core.Help.IHelpIndex, SqliteHelpIndex>();
		services.AddSingleton<App.Shared.Services.Help.HelpEmbeddingClient>();
		services.AddScoped<App.Core.Help.IHelpAskService, App.Shared.Services.Help.HelpAskService>();
		services.AddSingleton<App.Core.Skills.ISkillStore, App.Shared.Services.Skills.PreferencesSkillStore>();
		services.AddSingleton<App.Core.Skills.ISkillRunLogStore, App.Shared.Services.Skills.SkillRunLogStore>();
		services.AddSingleton<App.Core.Skills.ISkillRunner, App.Shared.Services.Skills.SkillRunner>();
		services.AddSingleton<App.Core.Workflows.IWorkflowStore, App.Shared.Services.Workflows.PreferencesWorkflowStore>();
		services.AddSingleton<App.Core.Workflows.IWorkflowOrchestrator, App.Shared.Services.Workflows.WorkflowOrchestrator>();
		services.AddSingleton<WorkflowDueHost>();
		services.AddSingleton<SettingsSyncStore>();
		services.AddSingleton<ISettingsSyncStore>(sp => sp.GetRequiredService<SettingsSyncStore>());
		services.AddSingleton<SipsorceryWebRtcTransport>();
		services.AddSingleton<IWebRtcTransport>(sp => sp.GetRequiredService<SipsorceryWebRtcTransport>());
		services.AddSingleton<MauiSyncService>();
		services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<MauiSyncService>());
		services.AddSingleton<INotesSyncBridge>(sp => sp.GetRequiredService<MauiSyncService>());
		services.AddSingleton<IGallerySyncBridge>(sp => sp.GetRequiredService<MauiSyncService>());
		services.AddSingleton<ICalendarSyncBridge>(sp => sp.GetRequiredService<MauiSyncService>());
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
		services.AddSingleton<MauiUrlEmbedOverlayService>();
		services.AddSingleton<IUrlEmbedOverlay>(sp => sp.GetRequiredService<MauiUrlEmbedOverlayService>());
#endif
#if LINUX_DESKTOP
		// Native WebKit overlay — required for Home Assistant (X-Frame-Options: SAMEORIGIN).
		services.AddSingleton<LinuxUrlEmbedOverlayService>();
		services.AddSingleton<IUrlEmbedOverlay>(sp => sp.GetRequiredService<LinuxUrlEmbedOverlayService>());
#endif
		services.AddSingleton<MauiPwaDetector>();
		services.AddSingleton<IPwaDetector>(sp => sp.GetRequiredService<MauiPwaDetector>());
		services.AddSingleton<SqliteKeyStore>();
		services.AddSingleton<IKeyStore>(sp => sp.GetRequiredService<SqliteKeyStore>());
		services.AddScoped<MauiCryptoService>();
		services.AddScoped<ICryptoService>(sp => sp.GetRequiredService<MauiCryptoService>());
		services.AddSingleton<IToolExecutionTrace, ToolExecutionTrace>();
		services.AddSingleton<IRoutingSessionStore, InMemoryRoutingSessionStore>();
		services.AddSingleton<ContextualRequestRouter>();
		services.AddSingleton<AiRequestRouter>();
		services.AddSingleton<IRequestRouter, CompositeRequestRouter>();
		services.AddSingleton<ISmartHomeService, HomeAssistantService>();
		services.AddSingleton<MauiBrowserContext>();
		services.AddSingleton<IBrowserContext>(sp => sp.GetRequiredService<MauiBrowserContext>());
		services.AddSingleton<McpToolSource>();
		services.AddSingleton<IMcpToolRefresher>(sp => sp.GetRequiredService<McpToolSource>());
		services.AddSingleton<OAuthReturnBridge>();
		services.AddSingleton<App.Core.UI.IAppNavigation, App.Shared.Services.AppNavigation>();
		services.AddSingleton<MauiOAuthInterceptor>();
		services.AddSingleton<IUriLauncher, MauiUriLauncher>();
		services.AddSingleton<App.Shared.Services.Connectors.ConnectorHttpExecutor>();
		services.AddSingleton<App.Shared.Services.Connectors.OpenApiConnectorToolSource>();
		services.AddSingleton<App.Core.Connectors.IOpenApiConnectorRefresher>(sp =>
			sp.GetRequiredService<App.Shared.Services.Connectors.OpenApiConnectorToolSource>());
		services.AddSingleton<App.Core.Tools.IConversationMediaBuffer, App.Shared.Services.Tools.ConversationMediaBuffer>();
		services.AddSingleton<App.Core.Tools.IToolConversationContext, App.Shared.Services.Tools.ToolConversationContext>();
		services.AddSingleton<NativeToolModule>();
		services.AddSingleton<HomeAssistantToolModule>();
		services.AddSingleton<BrowserAgentToolModule>();
		services.AddSingleton<App.Shared.Services.Tools.LemonadeToolModule>();
		services.AddSingleton<App.Shared.Services.Tools.CloudToolModule>();
		services.AddSingleton<App.Shared.Services.Tools.GalleryToolModule>();
		services.AddSingleton<App.Shared.Services.Tools.CalendarToolModule>();
		services.AddSingleton<App.Shared.Services.Tools.NotesToolModule>();
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<NativeToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<HomeAssistantToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<BrowserAgentToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<App.Shared.Services.Tools.LemonadeToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<App.Shared.Services.Tools.CloudToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<App.Shared.Services.Tools.GalleryToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<App.Shared.Services.Tools.CalendarToolModule>());
		services.AddSingleton<IToolModule>(sp => sp.GetRequiredService<App.Shared.Services.Tools.NotesToolModule>());
		services.AddSingleton<IToolProvider, CompositeToolProvider>();
		services.AddSingleton<ChatModelCatalogService>();
		services.AddSingleton<IChatModelCatalog>(sp => sp.GetRequiredService<ChatModelCatalogService>());
		services.AddSingleton<ChatCompletionService>();
		services.AddSingleton<IChatCompletionService>(sp => sp.GetRequiredService<ChatCompletionService>());
		services.AddSingleton<App.Core.Lemonade.ILemonadeImageService, App.Shared.Services.Lemonade.LemonadeImageService>();
		services.AddSingleton<App.Core.Lemonade.ILemonadeSpeechService, App.Shared.Services.Lemonade.LemonadeSpeechService>();
		services.AddSingleton<App.Core.Cloud.ICloudImageService, App.Shared.Services.Cloud.CloudImageService>();
		services.AddSingleton<App.Core.Cloud.ICloudSpeechService, App.Shared.Services.Cloud.CloudSpeechService>();
		services.AddSingleton<ChatAuthService>();
		services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<ChatAuthService>());
		services.AddHttpClient();
		services.AddSingleton<MauiUpdateService>();
		services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<MauiUpdateService>());
#if WINDOWS || LINUX_DESKTOP
		services.AddSingleton<HomeserverInstallService>();
		services.AddSingleton<IHomeserverInstallService>(sp =>
		{
			var svc = sp.GetRequiredService<HomeserverInstallService>();
			var flags = sp.GetService<HomeserverLaunchFlags>();
			if (flags is not null)
			{
				if (flags.IsFirstRun)
					svc.ShouldPromptOnStartup = true;
				if (flags.AfterUpdate || File.Exists(HomeserverPaths.PendingUpdateFlagPath))
					svc.PendingUpdateCheck = true;
			}
			return svc;
		});
		services.AddSingleton<LemonadeInstallService>();
		services.AddSingleton<ILemonadeInstallService>(sp => sp.GetRequiredService<LemonadeInstallService>());
		services.AddSingleton<OllamaInstallService>();
		services.AddSingleton<IOllamaInstallService>(sp => sp.GetRequiredService<OllamaInstallService>());
#else
		services.AddSingleton<IHomeserverInstallService>(_ => NullHomeserverInstallService.Instance);
		services.AddSingleton<ILemonadeInstallService>(_ => NullLemonadeInstallService.Instance);
		services.AddSingleton<IOllamaInstallService>(_ => NullOllamaInstallService.Instance);
#endif
		// Mobile / other TFMs may not register ISetupWizardHost above.
		if (services.All(d => d.ServiceType != typeof(ISetupWizardHost)))
			services.AddSingleton<ISetupWizardHost>(_ => NullSetupWizardHost.Instance);
	}

	/// <summary>Velopack lifecycle flags captured before DI is built.</summary>
	internal sealed record HomeserverLaunchFlags(bool IsFirstRun, bool AfterUpdate);

	private static void RestoreAuthCookies(IServiceProvider services)
	{
		var cookieStore = services.GetRequiredService<MauiAuthCookieStore>();
		var serverOptions = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppServerOptions>>().Value;
		cookieStore.Configure(serverOptions);
		cookieStore.EnsureLoadedAsync().GetAwaiter().GetResult();
	}

	/// <summary>
	/// Hub connect without waiting on first Blazor render (hide-to-tray / start-minimized).
	/// <see cref="App.Shared.Components.SyncConnectionBootstrap"/> still owns login-while-running.
	/// </summary>
	private static async Task StartMauiSyncAsync(IServiceProvider sp)
	{
		try
		{
			var auth = sp.GetRequiredService<IAuthService>();
			var sync = sp.GetRequiredService<ISyncService>();
			await auth.LoadAsync();
			await sync.InitializeAsync();
			if (auth.IsAuthenticated)
				await sync.EnsureConnectedAndRegisteredAsync();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[MauiSync] startup connect failed: {ex.Message}");
		}
	}
}
