using App.Core.Connectors;

namespace App.Maui;

public partial class MauiShell : Application
{
	private readonly OAuthReturnBridge? _oauthReturn;
#if WINDOWS
	private readonly WindowsDesktopHost? _desktop;
#endif

	// DI-preferred ctor (OAuth deep-link handoff). Parameterless fallback for tooling.
	public MauiShell(
		OAuthReturnBridge? oauthReturn = null
#if WINDOWS
		, WindowsDesktopHost? desktop = null
#endif
		)
	{
#if WINDOWS
		var userDataFolder = Path.Combine(App.Maui.Services.MauiAppData.Directory, "WebView2");
		Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
		_desktop = desktop;
#endif
		_oauthReturn = oauthReturn;
		InitializeComponent();
	}

	protected override void OnResume()
	{
		base.OnResume();
#if WINDOWS
		_desktop?.OnPowerResume();
#endif
	}

	/// <summary>Deep link / protocol activation (e.g. wizionic://oauth?oauth_session=...).</summary>
	protected override void OnAppLinkRequestReceived(Uri uri)
	{
		Console.WriteLine($"[Maui] AppLink received: {uri}");
		_oauthReturn?.SetFromUri(uri);
		base.OnAppLinkRequestReceived(uri);
	}

	protected override Window CreateWindow(IActivationState? activationState)
		=> CreateDesktopWindow();

	internal Window CreateDesktopWindow()
	{
		var window = new Window(new MainPage()) { Title = "Wizionic" };

#if WINDOWS
		// MAUI Windows uses a client-area title bar (content extends into the caption).
		// AppWindow.SetIcon alone often never paints a glyph there — use Window.TitleBar.Icon.
		// See: https://learn.microsoft.com/dotnet/maui/user-interface/controls/titlebar
		window.TitleBar = new TitleBar
		{
			Title = "Wizionic",
			// MauiImage: Resources/Images/titlebar_icon.png → "titlebar_icon.png"
			Icon = "titlebar_icon.png",
			HeightRequest = 32,
		};

		// Still set the Win32/AppWindow icon (taskbar / Alt-Tab / some shell surfaces).
		window.Created += OnWindowsWindowCreated;
#endif
		return window;
	}

#if WINDOWS
	internal void OpenAdditionalWindow()
		=> OpenWindow(CreateDesktopWindow());

	private void OnWindowsWindowCreated(object? sender, EventArgs e)
	{
		if (sender is not Window window)
			return;

		try
		{
			ApplyWindowsAppWindowIcon(window);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Desktop] Windows AppWindow icon failed: {ex.Message}");
		}
	}

	private void ApplyWindowsAppWindowIcon(Window window)
	{
		var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
		if (nativeWindow is null)
		{
			void OnHandlerChanged(object? s, EventArgs args)
			{
				window.HandlerChanged -= OnHandlerChanged;
				try { ApplyWindowsAppWindowIcon(window); }
				catch (Exception ex)
				{
					Console.WriteLine($"[Desktop] Windows AppWindow icon (retry) failed: {ex.Message}");
				}
			}
			window.HandlerChanged += OnHandlerChanged;
			return;
		}

		var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
		if (hwnd == IntPtr.Zero)
			return;

		var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
		var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
		if (appWindow is null)
			return;

		var iconPath = WindowsIconPath.Resolve();
		if (iconPath is null)
			Console.WriteLine("[Desktop] no appicon.ico found next to binary");
		else
		{
			appWindow.SetIcon(iconPath);
			Console.WriteLine($"[Desktop] Windows AppWindow icon set from {iconPath}");
		}

		_desktop?.Attach(window, appWindow);
	}
#endif
}
