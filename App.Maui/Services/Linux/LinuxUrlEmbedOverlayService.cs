#if LINUX_DESKTOP
using App.Core.UI;

namespace App.Maui.Services.Linux;

/// <summary>
/// Native WebKit overlay for sites that refuse iframes (Home Assistant X-Frame-Options).
/// Bounds are applied by <see cref="LinuxBrowserHost"/> the same way as the chat browser overlays.
/// </summary>
public sealed class LinuxUrlEmbedOverlayService : IUrlEmbedOverlay
{
	private readonly object _gate = new();
	private object? _owner;
	private string? _url;
	private bool _visible;
	private bool _suppressed;
	private LinuxBrowserOverlayService.Bounds _bounds;

	public bool IsNative => true;

	public string? Url
	{
		get { lock (_gate) return _url; }
	}

	/// <summary>True after Show until Hide — even if bounds are not ready yet.</summary>
	public bool Requested
	{
		get { lock (_gate) return _visible; }
	}

	public bool Visible
	{
		get { lock (_gate) return _visible && !_suppressed && _bounds.IsValid; }
	}

	public LinuxBrowserOverlayService.Bounds Bounds
	{
		get { lock (_gate) return _bounds; }
	}

	public event Action? Changed;

	public void Show(string url, object? owner = null)
	{
		url = (url ?? "").Trim();
		if (string.IsNullOrWhiteSpace(url))
		{
			Hide(owner);
			return;
		}

		if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
		    && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			url = "http://" + url;

		bool changed;
		lock (_gate)
		{
			_owner = owner;
			changed = !string.Equals(_url, url, StringComparison.OrdinalIgnoreCase) || !_visible;
			_url = url;
			_visible = true;
		}

		if (changed)
		{
			Console.WriteLine($"[UrlEmbed] Linux navigate {url}");
			Changed?.Invoke();
		}
	}

	public void Hide(object? owner = null)
	{
		lock (_gate)
		{
			if (!IsCurrentOwner(owner))
			{
				Console.WriteLine("[UrlEmbed] Linux Hide ignored — overlay owned by another embed");
				return;
			}

			_visible = false;
			_url = null;
			_owner = null;
			_bounds = default;
		}

		Changed?.Invoke();
	}

	public void UpdateBounds(double x, double y, double width, double height, object? owner = null)
	{
		var next = new LinuxBrowserOverlayService.Bounds(
			(int)Math.Round(x),
			(int)Math.Round(y),
			(int)Math.Round(width),
			(int)Math.Round(height));

		bool changed;
		lock (_gate)
		{
			if (!IsCurrentOwner(owner))
				return;

			if (!_visible)
				return;

			if (!next.IsValid)
			{
				changed = _bounds.IsValid;
				_bounds = default;
			}
			else
			{
				changed = _bounds != next;
				_bounds = next;
			}
		}

		if (changed)
			Changed?.Invoke();
	}

	public void SetSuppressed(bool suppressed)
	{
		bool changed;
		lock (_gate)
		{
			changed = _suppressed != suppressed;
			_suppressed = suppressed;
		}

		if (changed)
			Changed?.Invoke();
	}

	private bool IsCurrentOwner(object? owner) =>
		owner == null || _owner == null || ReferenceEquals(_owner, owner);
}
#endif
