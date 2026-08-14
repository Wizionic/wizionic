#if LINUX_DESKTOP
using System.Threading;

namespace App.Maui.Services.Linux;

/// <summary>
/// Hosts the Blazor shell plus native WebKit browser overlays in a Gtk.Overlay.
/// </summary>
/// <remarks>
/// Layout approach (tried and rejected alternatives):
/// <list type="bullet">
/// <item>WidthRequest alone — GTK treats it as a <em>minimum</em>; WebKit expands and covers side panels.</item>
/// <item>OnGetChildPosition — GirCore does not reliably write Allocation back to the native
/// GdkRectangle; returning true left the WebView full-window (no chrome).</item>
/// </list>
/// Current approach: wrap each WebView in a sized Gtk.Box clamp (Overflow=Hidden) and
/// position the clamp with MarginStart/Top. The clamp's SetSizeRequest is both min and
/// natural size, so Overlay allocates exactly that rectangle.
/// </remarks>
public sealed class LinuxBrowserHost
{
	private readonly LinuxBrowserAgentService _mainAgent;
	private readonly LinuxSideBrowserService _sideAgent;
	private readonly LinuxBrowserOverlayService _overlay;
	private readonly LinuxUrlEmbedOverlayService _urlEmbed;
	private readonly LinuxBrowserPlatformHooks _platformHooks;

	private Gtk.Overlay? _overlayWidget;
	private WebKit.WebView? _mainWebView;
	private WebKit.WebView? _sideWebView;
	private WebKit.WebView? _urlEmbedWebView;
	private Gtk.Box? _mainClamp;
	private Gtk.Box? _sideClamp;
	private Gtk.Box? _urlEmbedClamp;
	private string? _urlEmbedLoaded;
	private Action? _urlEmbedChangedHandler;
	private Gtk.Window? _window;
	private Adw.ToolbarView? _toolbarView;

	private Action? _overlayChangedHandler;
	private GObject.SignalHandler<GObject.Object, GObject.Object.NotifySignalArgs>? _windowNotifyHandler;
	private GObject.SignalHandler<Gdk.Surface, Gdk.Surface.LayoutSignalArgs>? _surfaceLayoutHandler;
	private Gdk.Surface? _surfaceHooked;
	private readonly List<object> _handlerRoots = [];

	private int _layoutQueued;
	private bool _htmlFullscreen;
	private bool _appWasFullscreen;
	private bool _topBarsWereRevealed = true;
	private int _overlayWidth;
	private int _overlayHeight;
	private int _fsRelayoutFrames;
	private uint _fsTickId;
	private int _lastLoggedFsW;
	private int _lastLoggedFsH;

	public LinuxBrowserHost(
		LinuxBrowserAgentService mainAgent,
		LinuxSideBrowserService sideAgent,
		LinuxBrowserOverlayService overlay,
		LinuxUrlEmbedOverlayService urlEmbed,
		LinuxBrowserPlatformHooks platformHooks)
	{
		_mainAgent = mainAgent;
		_sideAgent = sideAgent;
		_overlay = overlay;
		_urlEmbed = urlEmbed;
		_platformHooks = platformHooks;
	}

	/// <summary>
	/// Builds the root window child: Blazor fills the window; WebKit overlays track Blazor bounds.
	/// </summary>
	public Gtk.Widget BuildRoot(WebKit.WebView blazorWebView, Gtk.Window? window = null)
	{
		_window = window;
		_mainWebView = WebKit.WebView.New();
		_sideWebView = WebKit.WebView.New();
		_urlEmbedWebView = WebKit.WebView.New();
		ConfigureWebView(_mainWebView);
		ConfigureWebView(_sideWebView);
		ConfigureWebView(_urlEmbedWebView);

		_mainAgent.AttachWebView(_mainWebView);
		_sideAgent.AttachWebView(_sideWebView);
		_platformHooks.Attach(_mainWebView, window, this);

		_mainClamp = CreateClamp(_mainWebView);
		_sideClamp = CreateClamp(_sideWebView);
		_urlEmbedClamp = CreateClamp(_urlEmbedWebView);

		_overlayWidget = Gtk.Overlay.New();
		blazorWebView.Hexpand = true;
		blazorWebView.Vexpand = true;
		_overlayWidget.SetChild(blazorWebView);

		_overlayWidget.AddOverlay(_mainClamp);
		_overlayWidget.AddOverlay(_sideClamp);
		_overlayWidget.AddOverlay(_urlEmbedClamp);

		// Do not measure overlay children into the window size.
		_overlayWidget.SetMeasureOverlay(_mainClamp, false);
		_overlayWidget.SetMeasureOverlay(_sideClamp, false);
		_overlayWidget.SetMeasureOverlay(_urlEmbedClamp, false);

		_overlayChangedHandler = OnOverlayChanged;
		_overlay.Changed += _overlayChangedHandler;
		_urlEmbedChangedHandler = OnOverlayChanged;
		_urlEmbed.Changed += _urlEmbedChangedHandler;

		try
		{
			// Gtk.Overlay has no OnResize in GirCore; re-measure on realize and whenever layout runs.
			_overlayWidget.OnRealize += (_, _) =>
			{
				RememberOverlaySize();
				if (_htmlFullscreen)
					ApplyLayout();
			};
		}
		catch { /* optional signals */ }

		ApplyLayout();
		Console.WriteLine("[Browser] Linux WebKit dual-overlay host ready (clamp + margin layout)");
		return _overlayWidget;
	}

	/// <summary>
	/// Wire Adwaita chrome so HTML5 fullscreen can hide the header bar and fill the monitor.
	/// Call after the ToolbarView is created and set as the window content.
	/// </summary>
	public void AttachChrome(Adw.ToolbarView toolbarView)
	{
		_toolbarView = toolbarView ?? throw new ArgumentNullException(nameof(toolbarView));
	}

	/// <summary>
	/// True OS + in-app fullscreen for HTML5 video (YouTube, etc.):
	/// hide Adwaita header, gtk_window_fullscreen, expand WebKit clamp to the full content area,
	/// and keep re-laying out as the window surface grows (Wayland fullscreen is async).
	/// </summary>
	public void EnterHtmlFullscreen()
	{
		if (_htmlFullscreen)
		{
			// Already in FS — still re-apply in case size changed.
			ScheduleFullscreenRelayout();
			return;
		}

		_htmlFullscreen = true;

		// 1) Drop window chrome so the content (and overlay) can use the full window.
		try
		{
			if (_toolbarView != null)
			{
				_topBarsWereRevealed = _toolbarView.RevealTopBars;
				_toolbarView.SetRevealTopBars(false);
				// Let content claim the full vertical space once bars are hidden.
				try { _toolbarView.SetExtendContentToTopEdge(true); } catch { /* older libadwaita */ }
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux hide chrome failed: {ex.Message}");
		}

		// 2) Ask the compositor for real monitor fullscreen (hides CSD decorations too).
		try
		{
			if (_window != null)
			{
				_appWasFullscreen = _window.IsFullscreen();
				if (!_appWasFullscreen)
					_window.Fullscreen();
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux window.Fullscreen failed: {ex.Message}");
		}

		try
		{
			_toolbarView?.QueueResize();
			_overlayWidget?.QueueResize();
			_window?.QueueResize();
		}
		catch { /* ignore */ }

		HookWindowSizeTracking();
		_lastLoggedFsW = _lastLoggedFsH = 0;
		ScheduleFullscreenRelayout();
		Console.WriteLine("[Browser] Linux HTML fullscreen enter (chrome hidden + window fullscreen)");
	}

	public void ExitHtmlFullscreen()
	{
		if (!_htmlFullscreen)
			return;

		_htmlFullscreen = false;
		StopFullscreenRelayout();
		UnhookWindowSizeTracking();

		// Restore window first so subsequent layout uses restored geometry.
		try
		{
			if (_window != null && !_appWasFullscreen && _window.IsFullscreen())
				_window.Unfullscreen();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux window.Unfullscreen failed: {ex.Message}");
		}
		finally
		{
			_appWasFullscreen = false;
		}

		try
		{
			if (_toolbarView != null)
			{
				try { _toolbarView.SetExtendContentToTopEdge(false); } catch { /* ignore */ }
				_toolbarView.SetRevealTopBars(_topBarsWereRevealed);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux restore chrome failed: {ex.Message}");
		}

		// Wayland unfullscreen is async — re-apply chrome bounds after a few frames.
		QueueLayout();
		GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, 50, () =>
		{
			RememberOverlaySize();
			ApplyLayout();
			return false;
		});
		GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, 200, () =>
		{
			RememberOverlaySize();
			ApplyLayout();
			return false;
		});

		Console.WriteLine("[Browser] Linux HTML fullscreen leave");
	}

	private void HookWindowSizeTracking()
	{
		if (_window == null)
			return;

		try
		{
			if (_windowNotifyHandler == null)
			{
				_windowNotifyHandler = OnWindowNotify;
				_window.OnNotify += _windowNotifyHandler;
				_handlerRoots.Add(_windowNotifyHandler);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux window notify hook failed: {ex.Message}");
		}

		try
		{
			var surface = _window.GetSurface();
			if (surface != null && !ReferenceEquals(surface, _surfaceHooked))
			{
				if (_surfaceHooked != null && _surfaceLayoutHandler != null)
				{
					try { _surfaceHooked.OnLayout -= _surfaceLayoutHandler; } catch { /* ignore */ }
				}

				_surfaceLayoutHandler ??= OnSurfaceLayout;
				surface.OnLayout += _surfaceLayoutHandler;
				_handlerRoots.Add(_surfaceLayoutHandler);
				_surfaceHooked = surface;
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux surface layout hook failed: {ex.Message}");
		}
	}

	private void UnhookWindowSizeTracking()
	{
		try
		{
			if (_window != null && _windowNotifyHandler != null)
				_window.OnNotify -= _windowNotifyHandler;
		}
		catch { /* ignore */ }

		try
		{
			if (_surfaceHooked != null && _surfaceLayoutHandler != null)
				_surfaceHooked.OnLayout -= _surfaceLayoutHandler;
		}
		catch { /* ignore */ }

		_surfaceHooked = null;
		// Keep handlers rooted / reusable for the next enter; only detach signals.
	}

	private void OnWindowNotify(GObject.Object sender, GObject.Object.NotifySignalArgs args)
	{
		if (!_htmlFullscreen)
			return;

		var name = args.Pspec?.GetName();
		// fullscreened flips after the compositor ack; default-* may also change.
		if (name is "fullscreened" or "default-width" or "default-height" or "maximized")
			ScheduleFullscreenRelayout();
	}

	private void OnSurfaceLayout(Gdk.Surface sender, Gdk.Surface.LayoutSignalArgs args)
	{
		if (!_htmlFullscreen)
			return;

		// Surface size is the true window pixel size after (un)fullscreen.
		if (args.Width > 1 && args.Height > 1)
		{
			// Prefer overlay allocation for clamp coords, but fall back to surface.
			RememberOverlaySize();
			if (_overlayWidth < args.Width * 0.9 || _overlayHeight < args.Height * 0.9)
			{
				// Overlay has not expanded yet (header still consuming space, or not reallocated).
				// Use surface size so the video fills the monitor immediately.
				_overlayWidth = args.Width;
				_overlayHeight = args.Height;
			}
		}

		ApplyLayout();
	}

	private void ScheduleFullscreenRelayout()
	{
		RememberOverlaySize();
		ApplyLayout();

		// Pump a few frames: Wayland fullscreen + Adwaita bar hide reallocate asynchronously.
		_fsRelayoutFrames = 0;
		if (_overlayWidget == null)
			return;

		if (_fsTickId != 0)
			return;

		try
		{
			_fsTickId = _overlayWidget.AddTickCallback(OnFullscreenTick);
		}
		catch
		{
			// Fallback: idle retries.
			GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, 16, () =>
			{
				if (!_htmlFullscreen)
					return false;
				RememberOverlaySize();
				ApplyLayout();
				_fsRelayoutFrames++;
				return _fsRelayoutFrames < 30;
			});
		}
	}

	private bool OnFullscreenTick(Gtk.Widget widget, Gdk.FrameClock clock)
	{
		if (!_htmlFullscreen)
		{
			_fsTickId = 0;
			return false;
		}

		RememberOverlaySize();
		ApplyLayout();
		_fsRelayoutFrames++;
		if (_fsRelayoutFrames >= 45)
		{
			_fsTickId = 0;
			return false;
		}

		return true;
	}

	private void StopFullscreenRelayout()
	{
		if (_fsTickId != 0 && _overlayWidget != null)
		{
			try { _overlayWidget.RemoveTickCallback(_fsTickId); } catch { /* ignore */ }
			_fsTickId = 0;
		}
		_fsRelayoutFrames = 0;
	}

	private void RememberOverlaySize()
	{
		// Prefer the largest reliable size we can see: overlay allocation, then window.
		var bestW = 0;
		var bestH = 0;

		try
		{
			if (_overlayWidget != null)
			{
				var w = Math.Max(_overlayWidget.GetWidth(), _overlayWidget.GetAllocatedWidth());
				var h = Math.Max(_overlayWidget.GetHeight(), _overlayWidget.GetAllocatedHeight());
				if (w > bestW) bestW = w;
				if (h > bestH) bestH = h;
			}
		}
		catch { /* ignore */ }

		try
		{
			if (_window != null)
			{
				var w = Math.Max(_window.GetWidth(), _window.GetAllocatedWidth());
				var h = Math.Max(_window.GetHeight(), _window.GetAllocatedHeight());
				if (w > bestW) bestW = w;
				if (h > bestH) bestH = h;

				var surface = _window.GetSurface();
				if (surface != null)
				{
					var sw = surface.GetWidth();
					var sh = surface.GetHeight();
					if (sw > bestW) bestW = sw;
					if (sh > bestH) bestH = sh;
				}
			}
		}
		catch { /* ignore */ }

		if (bestW > 1 && bestH > 1)
		{
			_overlayWidth = bestW;
			_overlayHeight = bestH;
		}
	}

	private static Gtk.Box CreateClamp(WebKit.WebView webView)
	{
		var clamp = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
		clamp.Hexpand = false;
		clamp.Vexpand = false;
		clamp.Halign = Gtk.Align.Start;
		clamp.Valign = Gtk.Align.Start;
		clamp.MarginStart = 0;
		clamp.MarginTop = 0;
		clamp.MarginEnd = 0;
		clamp.MarginBottom = 0;
		clamp.Overflow = Gtk.Overflow.Hidden;
		clamp.Visible = false;
		clamp.SetSizeRequest(1, 1);

		webView.Hexpand = true;
		webView.Vexpand = true;
		webView.Halign = Gtk.Align.Fill;
		webView.Valign = Gtk.Align.Fill;
		clamp.Append(webView);
		return clamp;
	}

	private static void ConfigureWebView(WebKit.WebView webView)
	{
		var settings = webView.GetSettings();
		settings.EnableDeveloperExtras = true;
		settings.EnableJavascript = true;
		settings.EnableWebgl = true;
		webView.SetSettings(settings);
	}

	private void OnOverlayChanged() => QueueLayout();

	private void QueueLayout()
	{
		if (Interlocked.Exchange(ref _layoutQueued, 1) == 1)
			return;

		GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
		{
			Interlocked.Exchange(ref _layoutQueued, 0);
			ApplyLayout();
			return false;
		});
	}

	private void ApplyLayout()
	{
		if (_mainClamp == null || _sideClamp == null || _urlEmbedClamp == null)
			return;

		if (_htmlFullscreen || _overlay.IsHtmlFullscreen)
		{
			RememberOverlaySize();
			var w = Math.Max(_overlayWidth, 1);
			var h = Math.Max(_overlayHeight, 1);
			// Cover entire window content area (header is hidden during HTML fullscreen).
			PlaceClamp(_mainClamp, new LinuxBrowserOverlayService.Bounds(0, 0, w, h), visible: true, "main-fs");
			PlaceClamp(_sideClamp, default, visible: false, "side-fs");
			PlaceClamp(_urlEmbedClamp, default, visible: false, "url-embed-fs");
			return;
		}

		PlaceClamp(_mainClamp, _overlay.MainBounds, _overlay.MainVisible, "main");
		PlaceClamp(_sideClamp, _overlay.SideBounds, _overlay.SideVisible, "side");
		PlaceUrlEmbed();
	}

	private void PlaceUrlEmbed()
	{
		if (_urlEmbedClamp == null || _urlEmbedWebView == null)
			return;

		var visible = _urlEmbed.Visible;
		var bounds = _urlEmbed.Bounds;
		PlaceClamp(_urlEmbedClamp, bounds, visible, "url-embed");

		var url = _urlEmbed.Url;
		// Only blank on Hide — a 0-size measure must not unload HA after we already navigated.
		if (!_urlEmbed.Requested || string.IsNullOrWhiteSpace(url))
		{
			if (_urlEmbedLoaded != null)
			{
				try { _urlEmbedWebView.LoadUri("about:blank"); } catch { /* ignore */ }
				_urlEmbedLoaded = null;
			}
			return;
		}

		if (!string.Equals(_urlEmbedLoaded, url, StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				_urlEmbedWebView.LoadUri(url);
				_urlEmbedLoaded = url;
				Console.WriteLine($"[UrlEmbed] Linux WebKit LoadUri {url}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[UrlEmbed] Linux LoadUri failed: {ex.Message}");
			}
		}
	}

	private void PlaceClamp(
		Gtk.Box clamp,
		LinuxBrowserOverlayService.Bounds bounds,
		bool visible,
		string label)
	{
		if (!visible || !bounds.IsValid)
		{
			if (clamp.Visible)
				clamp.Visible = false;
			return;
		}

		// Exact placement relative to the overlay (same origin as Blazor viewport).
		if (clamp.MarginStart != bounds.X)
			clamp.MarginStart = bounds.X;
		if (clamp.MarginTop != bounds.Y)
			clamp.MarginTop = bounds.Y;

		// SetSizeRequest sets both minimum and natural size so Overlay measures
		// exactly this box and does not let WebKit expand past the host.
		clamp.SetSizeRequest(bounds.Width, bounds.Height);

		if (!clamp.Visible)
			clamp.Visible = true;

		// Force WebKit to notice the new allocation (important after window fullscreen).
		try
		{
			clamp.QueueResize();
			clamp.QueueAllocate();
		}
		catch { /* ignore */ }

		// Avoid spamming the console on every fullscreen tick frame.
		if (label is "main-fs" or "side-fs")
		{
			if (bounds.Width == _lastLoggedFsW && bounds.Height == _lastLoggedFsH)
				return;
			_lastLoggedFsW = bounds.Width;
			_lastLoggedFsH = bounds.Height;
		}

		Console.WriteLine(
			$"[Browser] overlay {label} -> x={bounds.X} y={bounds.Y} w={bounds.Width} h={bounds.Height}");
	}
}
#endif
