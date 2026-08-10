using System.Collections.Concurrent;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using App.Maui.Services.Linux;
using Microsoft.Extensions.DependencyInjection;
using WebKit.BlazorWebView.GirCore;

namespace App.Maui;

/// <summary>
/// Linux desktop entry point: GirCore Adwaita host + WebKit.BlazorWebView.GirCore
/// with native WebKit browser overlays for EmbeddedBrowser.
/// </summary>
[UnsupportedOSPlatform("windows")]
[UnsupportedOSPlatform("OSX")]
public static class Program
{
	private static Adw.Application? _application;
	private static Adw.ApplicationWindow? _window;
	private static BlazorWebView? _webView;
	private static Gtk.Widget? _root;
	private static IServiceProvider? _serviceProvider;

	private static readonly GObject.SignalHandler<Gio.Application> OnActivateHandler = OnActivate;
	private static readonly GObject.SignalHandler<Gio.Application> OnShutdownHandler = OnShutdown;

	private static GCHandle _applicationPin;
	private static GCHandle _windowPin;
	private static GCHandle _webViewPin;
	private static GCHandle _rootPin;
	private static GCHandle _servicesPin;
	private static GCHandle _activatePin;
	private static GCHandle _shutdownPin;

	private static readonly ConcurrentBag<object> LifetimeRoots = new();

	public static int Main(string[] args)
	{
		try
		{
			GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
		}
		catch
		{
			// Ignore if the runtime rejects the mode.
		}

		Adw.Module.Initialize();
		Gtk.Module.Initialize();
		WebKit.Module.Initialize();

		_application = Adw.Application.New("com.wizionic.app", Gio.ApplicationFlags.FlagsNone);
		Pin(_application, ref _applicationPin);
		LifetimeRoots.Add(_application);

		_application.OnActivate += OnActivateHandler;
		_application.OnShutdown += OnShutdownHandler;
		_activatePin = GCHandle.Alloc(OnActivateHandler);
		_shutdownPin = GCHandle.Alloc(OnShutdownHandler);
		LifetimeRoots.Add(OnActivateHandler);
		LifetimeRoots.Add(OnShutdownHandler);

		return _application.RunWithSynchronizationContext(args);
	}

	private static void OnActivate(Gio.Application sender, EventArgs args)
	{
		var app = (Adw.Application)sender;

		_window = Adw.ApplicationWindow.New(app);
		_window.Title = "Wizionic";
		_window.SetDefaultSize(1280, 800);

		// Window chrome (min/max/close). AdwApplicationWindow rejects gtk_window_set_titlebar;
		// use ToolbarView + HeaderBar instead of Window.Titlebar.
		_window.Decorated = true;
		_window.Deletable = true;
		_window.Resizable = true;

		Pin(_window, ref _windowPin);
		LifetimeRoots.Add(_window);

		_serviceProvider = MauiProgram.CreateLinuxServiceProvider();
		Pin(_serviceProvider, ref _servicesPin);
		LifetimeRoots.Add(_serviceProvider);

		_webView = new BlazorWebView(_serviceProvider);
#if DEBUG
		_webView.GetSettings().EnableDeveloperExtras = true;
#endif
		Pin(_webView, ref _webViewPin);
		LifetimeRoots.Add(_webView);

		// Overlay: Blazor fills the content area; WebKit browser views track #browser-content-host.
		var browserHost = _serviceProvider.GetRequiredService<LinuxBrowserHost>();
		var browserRoot = browserHost.BuildRoot(_webView, _window);
		LifetimeRoots.Add(browserRoot);
		LifetimeRoots.Add(browserHost);

		var headerBar = Adw.HeaderBar.New();
		headerBar.ShowTitle = true;
		headerBar.ShowEndTitleButtons = true;
		headerBar.ShowStartTitleButtons = true;
		// Empty start list; minimize/maximize/close on the end (right).
		// For left-side buttons use: "close,minimize,maximize:"
		headerBar.DecorationLayout = ":minimize,maximize,close";

		// App logo in the title bar (Adwaita does not auto-show the window icon like WinUI).
		try
		{
			var iconPath = LinuxDesktopIcon.ResolveIconPathPublic();
			if (iconPath is not null)
			{
				var logo = Gtk.Image.NewFromFile(iconPath);
				logo.SetPixelSize(22);
				logo.SetMarginStart(6);
				logo.SetMarginEnd(4);
				headerBar.PackStart(logo);
				LifetimeRoots.Add(logo);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Desktop] header logo failed: {ex.Message}");
		}

		LifetimeRoots.Add(headerBar);

		var toolbarView = Adw.ToolbarView.New();
		toolbarView.AddTopBar(headerBar);
		toolbarView.SetContent(browserRoot);
		LifetimeRoots.Add(toolbarView);

		// So HTML5/YouTube fullscreen can hide the header and fill the monitor.
		browserHost.AttachChrome(toolbarView);

		_root = toolbarView;
		Pin(_root, ref _rootPin);

		_window.SetContent(_root);

		// Dock / launch-bar icon (MauiIcon does not apply on plain net10.0 Linux).
		LinuxDesktopIcon.Apply(_window);

		_window.Present();

		GC.KeepAlive(_application);
		GC.KeepAlive(_window);
		GC.KeepAlive(_webView);
		GC.KeepAlive(_root);
		GC.KeepAlive(_serviceProvider);
		GC.KeepAlive(OnActivateHandler);
		GC.KeepAlive(LifetimeRoots);
	}

	private static void OnShutdown(Gio.Application sender, EventArgs args)
	{
		FreePin(ref _webViewPin);
		FreePin(ref _rootPin);
		FreePin(ref _windowPin);
		FreePin(ref _servicesPin);
	}

	private static void Pin(object value, ref GCHandle handle)
	{
		if (handle.IsAllocated)
			handle.Free();
		handle = GCHandle.Alloc(value);
	}

	private static void FreePin(ref GCHandle handle)
	{
		if (handle.IsAllocated)
			handle.Free();
		handle = default;
	}
}
