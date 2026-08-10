namespace App.Maui;

public partial class MauiShell : Application
{
	public MauiShell()
	{
#if WINDOWS
		var userDataFolder = Path.Combine(FileSystem.AppDataDirectory, "WebView2");
		Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
#endif
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
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
	private static void OnWindowsWindowCreated(object? sender, EventArgs e)
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

	private static void ApplyWindowsAppWindowIcon(Window window)
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

		var iconPath = ResolveWindowsIconPath();
		if (iconPath is null)
		{
			Console.WriteLine("[Desktop] no appicon.ico found next to binary");
			return;
		}

		appWindow.SetIcon(iconPath);
		Console.WriteLine($"[Desktop] Windows AppWindow icon set from {iconPath}");
	}

	private static string? ResolveWindowsIconPath()
	{
		var baseDir = AppContext.BaseDirectory;
		var candidates = new[]
		{
			Path.Combine(baseDir, "appicon.ico"),
			Path.Combine(baseDir, "Resources", "AppIcon", "appicon.ico"),
			Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Resources", "AppIcon", "appicon.ico")),
		};

		foreach (var path in candidates)
		{
			try
			{
				if (File.Exists(path))
					return path;
			}
			catch
			{
				// skip
			}
		}

		return null;
	}
#endif
}
