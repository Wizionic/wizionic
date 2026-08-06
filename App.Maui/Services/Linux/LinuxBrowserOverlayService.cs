#if LINUX_DESKTOP
using App.Core.Browser;

namespace App.Maui.Services.Linux;

/// <summary>
/// Positions native WebKit overlays over Blazor-measured browser content hosts.
/// </summary>
public sealed class LinuxBrowserOverlayService : IBrowserOverlaySync
{
	private readonly object _gate = new();

	public record struct Bounds(int X, int Y, int Width, int Height)
	{
		public bool IsValid => Width > 1 && Height > 1;
	}

	private Bounds _main;
	private Bounds _side;
	private bool _mainVisible;
	private bool _sideVisible;
	private bool _htmlFullscreen;
	private bool _sideWasVisibleBeforeFullscreen;

	public bool IsHtmlFullscreen
	{
		get { lock (_gate) return _htmlFullscreen; }
	}

	public event Action? Changed;

	/// <summary>
	/// Expand main overlay to fill the entire Gtk.Overlay so HTML5 fullscreen
	/// covers the whole app (not just the browser content host).
	/// </summary>
	public void EnterHtmlFullscreen()
	{
		bool changed;
		lock (_gate)
		{
			if (_htmlFullscreen)
				return;
			_htmlFullscreen = true;
			_sideWasVisibleBeforeFullscreen = _sideVisible;
			_sideVisible = false;
			_mainVisible = true;
			changed = true;
		}

		if (changed)
			Changed?.Invoke();
	}

	public void ExitHtmlFullscreen()
	{
		bool changed;
		lock (_gate)
		{
			if (!_htmlFullscreen)
				return;
			_htmlFullscreen = false;
			_mainVisible = _main.IsValid;
			_sideVisible = _sideWasVisibleBeforeFullscreen && _side.IsValid;
			changed = true;
		}

		if (changed)
			Changed?.Invoke();
	}

	public Bounds MainBounds
	{
		get { lock (_gate) return _main; }
	}

	public Bounds SideBounds
	{
		get { lock (_gate) return _side; }
	}

	public bool MainVisible
	{
		get { lock (_gate) return _mainVisible; }
	}

	public bool SideVisible
	{
		get { lock (_gate) return _sideVisible; }
	}

	public void ReportMainBounds(double x, double y, double width, double height) =>
		Report(isMain: true, x, y, width, height);

	public void ReportSideBounds(double x, double y, double width, double height) =>
		Report(isMain: false, x, y, width, height);

	public void SetMainOverlayVisible(bool visible) => SetVisible(isMain: true, visible);

	public void SetSideOverlayVisible(bool visible) => SetVisible(isMain: false, visible);

	public void RestoreCachedOverlay()
	{
		bool changed;
		lock (_gate)
		{
			var mainWas = _mainVisible;
			var sideWas = _sideVisible;
			_mainVisible = _main.IsValid;
			_sideVisible = _side.IsValid;
			changed = mainWas != _mainVisible || sideWas != _sideVisible;
		}

		if (changed)
			Changed?.Invoke();
	}

	public bool TryGetPosition(Gtk.Widget widget, WebKit.WebView mainView, WebKit.WebView sideView, out int x, out int y, out int w, out int h)
	{
		lock (_gate)
		{
			if (ReferenceEquals(widget, mainView) && _mainVisible && _main.IsValid)
			{
				x = _main.X;
				y = _main.Y;
				w = _main.Width;
				h = _main.Height;
				return true;
			}

			if (ReferenceEquals(widget, sideView) && _sideVisible && _side.IsValid)
			{
				x = _side.X;
				y = _side.Y;
				w = _side.Width;
				h = _side.Height;
				return true;
			}
		}

		x = y = w = h = 0;
		return false;
	}

	private void Report(bool isMain, double x, double y, double width, double height)
	{
		var next = new Bounds(
			(int)Math.Round(x),
			(int)Math.Round(y),
			(int)Math.Round(width),
			(int)Math.Round(height));

		bool changed;
		lock (_gate)
		{
			// While HTML fullscreen is active, still cache chrome bounds but do not hide main.
			if (_htmlFullscreen && isMain)
			{
				if (next.IsValid)
					_main = next;
				changed = false;
			}
			else if (!next.IsValid)
			{
				if (isMain)
				{
					changed = _mainVisible;
					_mainVisible = false;
				}
				else
				{
					changed = _sideVisible;
					_sideVisible = false;
				}
			}
			else if (isMain)
			{
				changed = _main != next || !_mainVisible;
				_main = next;
				_mainVisible = true;
			}
			else
			{
				changed = _side != next || !_sideVisible;
				_side = next;
				_sideVisible = true;
			}
		}

		if (changed)
		{
			Console.WriteLine(
				$"[Browser] bounds {(isMain ? "main" : "side")} " +
				$"x={next.X} y={next.Y} w={next.Width} h={next.Height} valid={next.IsValid}");
			Changed?.Invoke();
		}
	}

	private void SetVisible(bool isMain, bool visible)
	{
		bool changed;
		lock (_gate)
		{
			if (_htmlFullscreen && isMain && !visible)
			{
				changed = false;
			}
			else if (isMain)
			{
				changed = _mainVisible != visible;
				_mainVisible = visible && _main.IsValid;
			}
			else
			{
				changed = _sideVisible != visible;
				_sideVisible = visible && _side.IsValid;
			}
		}

		if (changed)
			Changed?.Invoke();
	}
}
#endif
