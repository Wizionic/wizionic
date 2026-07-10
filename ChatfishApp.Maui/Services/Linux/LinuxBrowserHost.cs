#if LINUX_DESKTOP
using System.Threading;

namespace ChatfishApp.Maui.Services.Linux;

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

	private Gtk.Overlay? _overlayWidget;
	private WebKit.WebView? _mainWebView;
	private WebKit.WebView? _sideWebView;
	private Gtk.Box? _mainClamp;
	private Gtk.Box? _sideClamp;

	private Action? _overlayChangedHandler;
	private int _layoutQueued;

	public LinuxBrowserHost(
		LinuxBrowserAgentService mainAgent,
		LinuxSideBrowserService sideAgent,
		LinuxBrowserOverlayService overlay)
	{
		_mainAgent = mainAgent;
		_sideAgent = sideAgent;
		_overlay = overlay;
	}

	/// <summary>
	/// Builds the root window child: Blazor fills the window; WebKit overlays track Blazor bounds.
	/// </summary>
	public Gtk.Widget BuildRoot(WebKit.WebView blazorWebView)
	{
		_mainWebView = WebKit.WebView.New();
		_sideWebView = WebKit.WebView.New();
		ConfigureWebView(_mainWebView);
		ConfigureWebView(_sideWebView);

		_mainAgent.AttachWebView(_mainWebView);
		_sideAgent.AttachWebView(_sideWebView);

		_mainClamp = CreateClamp(_mainWebView);
		_sideClamp = CreateClamp(_sideWebView);

		_overlayWidget = Gtk.Overlay.New();
		blazorWebView.Hexpand = true;
		blazorWebView.Vexpand = true;
		_overlayWidget.SetChild(blazorWebView);

		_overlayWidget.AddOverlay(_mainClamp);
		_overlayWidget.AddOverlay(_sideClamp);

		// Do not measure overlay children into the window size.
		_overlayWidget.SetMeasureOverlay(_mainClamp, false);
		_overlayWidget.SetMeasureOverlay(_sideClamp, false);

		_overlayChangedHandler = OnOverlayChanged;
		_overlay.Changed += _overlayChangedHandler;

		ApplyLayout();
		Console.WriteLine("[Browser] Linux WebKit dual-overlay host ready (clamp + margin layout)");
		return _overlayWidget;
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

	private void OnOverlayChanged()
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
		if (_mainClamp == null || _sideClamp == null)
			return;

		PlaceClamp(_mainClamp, _overlay.MainBounds, _overlay.MainVisible, "main");
		PlaceClamp(_sideClamp, _overlay.SideBounds, _overlay.SideVisible, "side");
	}

	private static void PlaceClamp(
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

		Console.WriteLine(
			$"[Browser] overlay {label} -> x={bounds.X} y={bounds.Y} w={bounds.Width} h={bounds.Height}");
	}
}
#endif
