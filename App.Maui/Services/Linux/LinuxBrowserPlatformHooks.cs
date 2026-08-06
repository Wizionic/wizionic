#if LINUX_DESKTOP
using System.Collections.Concurrent;
using App.Core.Browser;

namespace App.Maui.Services.Linux;

/// <summary>
/// Linux WebKit hooks for downloads (save dialog + tracking) and HTML fullscreen
/// (expand overlay + Gdk/Gtk window fullscreen).
/// </summary>
public sealed class LinuxBrowserPlatformHooks
{
	private readonly IBrowserStore _store;
	private readonly IBrowserDownloadService _downloads;
	private readonly LinuxBrowserOverlayService _overlay;

	private WebKit.WebView? _webView;
	private WebKit.NetworkSession? _networkSession;
	private Gtk.Window? _window;
	private LinuxBrowserHost? _host;

	private GObject.ReturningSignalHandler<WebKit.WebView, WebKit.WebView.DecidePolicySignalArgs, bool>? _decidePolicyHandler;
	// WebKitGTK 6 / GirCore: enter/leave-fullscreen are ReturningSignalHandler<WebView, bool>
	// (Invoke uses System.EventArgs — not SignalArgs), not ReturningSignalHandler`3.
	private GObject.ReturningSignalHandler<WebKit.WebView, bool>? _enterFullscreenHandler;
	private GObject.ReturningSignalHandler<WebKit.WebView, bool>? _leaveFullscreenHandler;
	// download-started moved from WebView → NetworkSession in WebKitGTK 6.
	private GObject.SignalHandler<WebKit.NetworkSession, WebKit.NetworkSession.DownloadStartedSignalArgs>? _downloadStartedHandler;

	private readonly ConcurrentDictionary<WebKit.Download, string> _downloadMap = new();
	// Root per-download signal handlers + Download instances (GirCore/GObject can drop them otherwise).
	private readonly ConcurrentDictionary<WebKit.Download, DownloadHooks> _liveDownloads = new();
	private bool _htmlFullscreen;

	// Keep signal handlers rooted for GirCore.
	private readonly List<object> _handlerRoots = [];

	private sealed class DownloadHooks
	{
		public required GObject.ReturningSignalHandler<WebKit.Download, WebKit.Download.DecideDestinationSignalArgs, bool> DecideDestination { get; init; }
		public required GObject.SignalHandler<WebKit.Download> Finished { get; init; }
		public required GObject.SignalHandler<WebKit.Download, WebKit.Download.FailedSignalArgs> Failed { get; init; }
		public required GObject.SignalHandler<WebKit.Download, WebKit.Download.ReceivedDataSignalArgs> ReceivedData { get; init; }
		public GObject.SignalHandler<WebKit.Download, WebKit.Download.CreatedDestinationSignalArgs>? CreatedDestination { get; init; }
	}

	public LinuxBrowserPlatformHooks(
		IBrowserStore store,
		IBrowserDownloadService downloads,
		LinuxBrowserOverlayService overlay)
	{
		_store = store;
		_downloads = downloads;
		_overlay = overlay;
	}

	public void Attach(WebKit.WebView webView, Gtk.Window? window, LinuxBrowserHost host)
	{
		Detach();
		_webView = webView ?? throw new ArgumentNullException(nameof(webView));
		_window = window;
		_host = host;

		try
		{
			// WebKitGTK 6: downloads are owned by NetworkSession, not WebView.
			_networkSession = _webView.GetNetworkSession();
			_downloadStartedHandler = OnDownloadStarted;
			_networkSession.OnDownloadStarted += _downloadStartedHandler;
			_handlerRoots.Add(_downloadStartedHandler);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux DownloadStarted attach failed: {ex.Message}");
		}

		try
		{
			_decidePolicyHandler = OnDecidePolicy;
			_webView.OnDecidePolicy += _decidePolicyHandler;
			_handlerRoots.Add(_decidePolicyHandler);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux DecidePolicy attach failed: {ex.Message}");
		}

		try
		{
			_enterFullscreenHandler = OnEnterFullscreen;
			_webView.OnEnterFullscreen += _enterFullscreenHandler;
			_handlerRoots.Add(_enterFullscreenHandler);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux EnterFullscreen attach failed: {ex.Message}");
		}

		try
		{
			_leaveFullscreenHandler = OnLeaveFullscreen;
			_webView.OnLeaveFullscreen += _leaveFullscreenHandler;
			_handlerRoots.Add(_leaveFullscreenHandler);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux LeaveFullscreen attach failed: {ex.Message}");
		}

		try
		{
			var settings = _webView.GetSettings();
			settings.EnableFullscreen = true;
			_webView.SetSettings(settings);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux EnableFullscreen failed: {ex.Message}");
		}

		Console.WriteLine("[Browser] Linux platform hooks attached (downloads + fullscreen)");
	}

	public void Detach()
	{
		try
		{
			if (_networkSession != null && _downloadStartedHandler != null)
				_networkSession.OnDownloadStarted -= _downloadStartedHandler;
		}
		catch { /* ignore */ }

		if (_webView != null)
		{
			try
			{
				if (_decidePolicyHandler != null)
					_webView.OnDecidePolicy -= _decidePolicyHandler;
				if (_enterFullscreenHandler != null)
					_webView.OnEnterFullscreen -= _enterFullscreenHandler;
				if (_leaveFullscreenHandler != null)
					_webView.OnLeaveFullscreen -= _leaveFullscreenHandler;
			}
			catch { /* ignore */ }
		}

		foreach (var dl in _liveDownloads.Keys.ToList())
			UnhookDownload(dl);
		_liveDownloads.Clear();
		_downloadMap.Clear();

		_downloadStartedHandler = null;
		_decidePolicyHandler = null;
		_enterFullscreenHandler = null;
		_leaveFullscreenHandler = null;
		_handlerRoots.Clear();
		_webView = null;
		_networkSession = null;
		_window = null;
		_host = null;
	}

	private void OnDownloadStarted(WebKit.NetworkSession sender, WebKit.NetworkSession.DownloadStartedSignalArgs args)
	{
		try
		{
			var download = args.Download;
			if (download == null)
				return;

			// Root handlers + download for the lifetime of the transfer (GirCore GC otherwise).
			var hooks = new DownloadHooks
			{
				DecideDestination = OnDecideDestination,
				Finished = OnDownloadFinished,
				Failed = OnDownloadFailed,
				ReceivedData = OnDownloadReceivedData,
				CreatedDestination = OnCreatedDestination
			};
			_liveDownloads[download] = hooks;

			download.OnDecideDestination += hooks.DecideDestination;
			download.OnFinished += hooks.Finished;
			download.OnFailed += hooks.Failed;
			download.OnReceivedData += hooks.ReceivedData;
			download.OnCreatedDestination += hooks.CreatedDestination;

			try { download.SetAllowOverwrite(true); } catch { /* optional */ }
			Console.WriteLine("[Browser] Linux download-started hooked");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux OnDownloadStarted failed: {ex.Message}");
		}
	}

	/// <summary>
	/// WebKitGTK 6 (2022 GLIB API): return true without SetDestination to handle async
	/// (file dialog), then call SetDestination with an absolute filesystem path — not file://.
	/// Nested main-loop dialogs inside this handler leave the download stuck forever.
	/// </summary>
	private bool OnDecideDestination(WebKit.Download download, WebKit.Download.DecideDestinationSignalArgs args)
	{
		try
		{
			var suggested = args.SuggestedFilename;
			if (string.IsNullOrWhiteSpace(suggested))
				suggested = "download";

			// Sanitize path separators from server-provided names.
			suggested = suggested.Replace('/', '_').Replace('\\', '_');

			if (_store.GetSettings().AskBeforeDownloading)
			{
				// Async path: keep download paused until Save dialog finishes.
				_ = ResolveDestinationAndStartAsync(download, suggested);
				return true;
			}

			var folder = BrowserDownloadService.GetDefaultDownloadsFolder();
			Directory.CreateDirectory(folder);
			var path = BrowserDownloadService.MakeUniquePath(Path.Combine(folder, suggested));
			ApplyDestination(download, path);
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux decide-destination failed: {ex.Message}");
			try { download.Cancel(); } catch { /* ignore */ }
			return true;
		}
	}

	private async Task ResolveDestinationAndStartAsync(WebKit.Download download, string suggestedFileName)
	{
		try
		{
			var path = await PickSavePathAsync(suggestedFileName).ConfigureAwait(true);
			if (string.IsNullOrWhiteSpace(path))
			{
				Console.WriteLine("[Browser] Linux download cancelled (no path)");
				try { download.Cancel(); } catch { /* ignore */ }
				return;
			}

			// Ensure we always have a file path (dialog may return a bare directory on some portals).
			path = EnsureFilePath(path, suggestedFileName);
			ApplyDestination(download, path);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux async destination failed: {ex.Message}");
			try { download.Cancel(); } catch { /* ignore */ }
		}
	}

	private void ApplyDestination(WebKit.Download download, string path)
	{
		// WebKitGTK 6 requires an absolute local path (NOT file:// URI). Passing a URI
		// hits g_return_if_fail(g_path_is_absolute) and the download never starts —
		// UI stays "InProgress" forever with no file written.
		var absolute = Path.GetFullPath(path);
		var dir = Path.GetDirectoryName(absolute);
		if (!string.IsNullOrWhiteSpace(dir))
			Directory.CreateDirectory(dir);

		try { download.SetAllowOverwrite(true); } catch { /* ignore */ }
		download.SetDestination(absolute);

		var item = _downloads.Begin(download.GetRequest()?.GetUri() ?? "", absolute, Path.GetFileName(absolute));
		_downloadMap[download] = item.Id;
		Console.WriteLine($"[Browser] Linux download destination set -> {absolute}");
	}

	private static string EnsureFilePath(string path, string suggestedFileName)
	{
		// If the portal/dialog only returned a directory, append the suggested name.
		try
		{
			if (Directory.Exists(path) && !File.Exists(path))
				return BrowserDownloadService.MakeUniquePath(Path.Combine(path, suggestedFileName));
		}
		catch { /* use as-is */ }
		return path;
	}

	private void OnCreatedDestination(WebKit.Download sender, WebKit.Download.CreatedDestinationSignalArgs args)
	{
		Console.WriteLine($"[Browser] Linux download file created: {args.Destination}");
	}

	private void OnDownloadFinished(WebKit.Download sender, EventArgs args)
	{
		if (!_downloadMap.TryRemove(sender, out var id))
		{
			UnhookDownload(sender);
			return;
		}
		try
		{
			var dest = sender.GetDestination();
			var path = NormalizeDestinationPath(dest) ?? dest;
			if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
			{
				_downloads.Complete(id, path);
				Console.WriteLine($"[Browser] Linux download finished: {path} ({new FileInfo(path).Length} bytes)");
			}
			else
			{
				// Finished can fire after failed/cancel; only mark complete if a file exists.
				var existing = _downloads.Downloads.FirstOrDefault(d => d.Id == id);
				if (existing is { State: BrowserDownloadState.InProgress })
				{
					if (!string.IsNullOrWhiteSpace(path))
						_downloads.Complete(id, path);
					else
						_downloads.Fail(id, "Download finished but no file was written");
				}
				Console.WriteLine($"[Browser] Linux download finished (path={path}, exists={path != null && File.Exists(path)})");
			}
		}
		catch (Exception ex)
		{
			_downloads.Fail(id, ex.Message);
		}
		UnhookDownload(sender);
	}

	private void OnDownloadFailed(WebKit.Download sender, WebKit.Download.FailedSignalArgs args)
	{
		if (!_downloadMap.TryRemove(sender, out var id))
		{
			// Destination may never have been applied (cancel before Begin).
			UnhookDownload(sender);
			return;
		}
		var msg = args.Error?.Message ?? "Download failed";
		// User cancel is expected when dismissing the save dialog.
		if (msg.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
		    || msg.Contains("canceled", StringComparison.OrdinalIgnoreCase))
		{
			_downloads.Cancel(id);
			Console.WriteLine($"[Browser] Linux download cancelled: {msg}");
		}
		else
		{
			_downloads.Fail(id, msg);
			Console.WriteLine($"[Browser] Linux download failed: {msg}");
		}
		UnhookDownload(sender);
	}

	private void OnDownloadReceivedData(WebKit.Download sender, WebKit.Download.ReceivedDataSignalArgs args)
	{
		if (!_downloadMap.TryGetValue(sender, out var id))
			return;
		try
		{
			_downloads.Update(id, item =>
			{
				item.BytesReceived = (long)sender.GetReceivedDataLength();
				// Estimated progress when response content-length is known.
				try
				{
					var response = sender.GetResponse();
					var len = response?.GetContentLength() ?? 0UL;
					if (len > 0 && len <= (ulong)long.MaxValue)
						item.TotalBytes = (long)len;
				}
				catch { /* optional */ }
			});
		}
		catch { /* ignore */ }
	}

	private void UnhookDownload(WebKit.Download download)
	{
		if (!_liveDownloads.TryRemove(download, out var hooks))
			return;
		try
		{
			download.OnDecideDestination -= hooks.DecideDestination;
			download.OnFinished -= hooks.Finished;
			download.OnFailed -= hooks.Failed;
			download.OnReceivedData -= hooks.ReceivedData;
			if (hooks.CreatedDestination != null)
				download.OnCreatedDestination -= hooks.CreatedDestination;
		}
		catch { /* ignore */ }
	}

	/// <summary>
	/// Ensure unsupported MIME types become downloads (triggers DownloadStarted).
	/// </summary>
	private bool OnDecidePolicy(WebKit.WebView sender, WebKit.WebView.DecidePolicySignalArgs args)
	{
		try
		{
			if (args.DecisionType != WebKit.PolicyDecisionType.Response)
				return false;

			if (args.Decision is not WebKit.ResponsePolicyDecision responseDecision)
				return false;

			if (responseDecision.IsMimeTypeSupported())
				return false;

			// Force download for unsupported MIME (zip, pdf attachment, etc.).
			responseDecision.Download();
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux DecidePolicy failed: {ex.Message}");
			return false;
		}
	}

	private bool OnEnterFullscreen(WebKit.WebView sender, EventArgs args)
	{
		try
		{
			_htmlFullscreen = true;
			// Expand overlay bounds flag, then host hides Adwaita chrome + OS-fullscreen + re-layout.
			_overlay.EnterHtmlFullscreen();
			_host?.EnterHtmlFullscreen();
			Console.WriteLine("[Browser] Linux HTML fullscreen enter (handled by host)");
			// true = we handle fullscreen presentation (must size WebView ourselves)
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux enter fullscreen failed: {ex.Message}");
			return false;
		}
	}

	private bool OnLeaveFullscreen(WebKit.WebView sender, EventArgs args)
	{
		try
		{
			if (!_htmlFullscreen)
				return false;
			_htmlFullscreen = false;
			// Host restores chrome + unfullscreens window; overlay restores side/main visibility.
			_host?.ExitHtmlFullscreen();
			_overlay.ExitHtmlFullscreen();
			Console.WriteLine("[Browser] Linux HTML fullscreen leave (handled by host)");
			return false;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux leave fullscreen failed: {ex.Message}");
			return false;
		}
	}

	private async Task<string?> PickSavePathAsync(string suggestedFileName)
	{
		try
		{
			if (_window == null)
			{
				var folder = BrowserDownloadService.GetDefaultDownloadsFolder();
				Directory.CreateDirectory(folder);
				return BrowserDownloadService.MakeUniquePath(Path.Combine(folder, suggestedFileName));
			}

			var dialog = Gtk.FileDialog.New();
			dialog.SetTitle("Save download");
			dialog.SetInitialName(suggestedFileName);

			try
			{
				var file = await dialog.SaveAsync(_window).ConfigureAwait(true);
				return file?.GetPath();
			}
			catch (Exception ex)
			{
				// User cancel raises; treat as null.
				if (!ex.Message.Contains("Dismissed", StringComparison.OrdinalIgnoreCase)
				    && !ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
				    && !ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase))
					Console.WriteLine($"[Browser] Linux save dialog error: {ex.Message}");
				return null;
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux PickSavePath failed: {ex.Message}");
			return null;
		}
	}

	private static string? NormalizeDestinationPath(string? dest)
	{
		if (string.IsNullOrWhiteSpace(dest))
			return null;
		try
		{
			if (dest.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
				return new Uri(dest).LocalPath;
			return dest;
		}
		catch
		{
			return dest;
		}
	}
}
#endif
