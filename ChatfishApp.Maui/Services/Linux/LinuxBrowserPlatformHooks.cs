#if LINUX_DESKTOP
using System.Collections.Concurrent;
using ChatfishApp.Core.Browser;

namespace ChatfishApp.Maui.Services.Linux;

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
	private Gtk.Window? _window;
	private LinuxBrowserHost? _host;

	private GObject.ReturningSignalHandler<WebKit.WebView, WebKit.WebView.DecidePolicySignalArgs, bool>? _decidePolicyHandler;
	private GObject.ReturningSignalHandler<WebKit.WebView, EventArgs, bool>? _enterFullscreenHandler;
	private GObject.SignalHandler<WebKit.WebView>? _leaveFullscreenHandler;
	private GObject.SignalHandler<WebKit.WebView, WebKit.WebView.DownloadStartedSignalArgs>? _downloadStartedHandler;

	private readonly ConcurrentDictionary<WebKit.Download, string> _downloadMap = new();
	private bool _htmlFullscreen;

	// Keep signal handlers rooted for GirCore.
	private readonly List<object> _handlerRoots = [];

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
			_downloadStartedHandler = OnDownloadStarted;
			_webView.OnDownloadStarted += _downloadStartedHandler;
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
		if (_webView != null)
		{
			try
			{
				if (_downloadStartedHandler != null)
					_webView.OnDownloadStarted -= _downloadStartedHandler;
				if (_decidePolicyHandler != null)
					_webView.OnDecidePolicy -= _decidePolicyHandler;
				if (_enterFullscreenHandler != null)
					_webView.OnEnterFullscreen -= _enterFullscreenHandler;
				if (_leaveFullscreenHandler != null)
					_webView.OnLeaveFullscreen -= _leaveFullscreenHandler;
			}
			catch { /* ignore */ }
		}

		_downloadStartedHandler = null;
		_decidePolicyHandler = null;
		_enterFullscreenHandler = null;
		_leaveFullscreenHandler = null;
		_handlerRoots.Clear();
		_webView = null;
		_window = null;
		_host = null;
		_downloadMap.Clear();
	}

	private void OnDownloadStarted(WebKit.WebView sender, WebKit.WebView.DownloadStartedSignalArgs args)
	{
		try
		{
			var download = args.Download;
			if (download == null)
				return;

			// decide-destination chooses the save path (and may show a dialog).
			download.OnDecideDestination += OnDecideDestination;
			download.OnFinished += OnDownloadFinished;
			download.OnFailed += OnDownloadFailed;
			download.OnReceivedData += OnDownloadReceivedData;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux OnDownloadStarted failed: {ex.Message}");
		}
	}

	private bool OnDecideDestination(WebKit.Download download, WebKit.Download.DecideDestinationSignalArgs args)
	{
		try
		{
			var suggested = args.SuggestedFilename;
			if (string.IsNullOrWhiteSpace(suggested))
				suggested = "download";

			string? path;
			if (_store.GetSettings().AskBeforeDownloading)
			{
				path = PickSavePathBlocking(suggested);
				if (string.IsNullOrWhiteSpace(path))
				{
					try { download.Cancel(); } catch { /* ignore */ }
					return true;
				}
			}
			else
			{
				var folder = BrowserDownloadService.GetDefaultDownloadsFolder();
				Directory.CreateDirectory(folder);
				path = BrowserDownloadService.MakeUniquePath(Path.Combine(folder, suggested));
			}

			var uri = PathToFileUri(path);
			download.SetDestination(uri);

			var item = _downloads.Begin(download.GetRequest()?.GetUri() ?? "", path, Path.GetFileName(path));
			_downloadMap[download] = item.Id;
			Console.WriteLine($"[Browser] Linux download -> {path}");
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux decide-destination failed: {ex.Message}");
			try { download.Cancel(); } catch { /* ignore */ }
			return true;
		}
	}

	private void OnDownloadFinished(WebKit.Download sender, EventArgs args)
	{
		if (!_downloadMap.TryRemove(sender, out var id))
			return;
		try
		{
			var dest = sender.GetDestination();
			var path = FileUriToPath(dest) ?? dest;
			_downloads.Complete(id, path);
			Console.WriteLine($"[Browser] Linux download finished: {path}");
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
			return;
		var msg = args.Error?.Message ?? "Download failed";
		_downloads.Fail(id, msg);
		Console.WriteLine($"[Browser] Linux download failed: {msg}");
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
					var len = response?.GetContentLength() ?? 0;
					if (len > 0)
						item.TotalBytes = len;
				}
				catch { /* optional */ }
			});
		}
		catch { /* ignore */ }
	}

	private static void UnhookDownload(WebKit.Download download)
	{
		try
		{
			// Handlers may already be gone; best-effort.
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
			_overlay.EnterHtmlFullscreen();
			_host?.EnterHtmlFullscreen();
			try { _window?.Fullscreen(); } catch { /* ignore */ }
			Console.WriteLine("[Browser] Linux HTML fullscreen enter");
			// true = we handle fullscreen presentation
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux enter fullscreen failed: {ex.Message}");
			return false;
		}
	}

	private void OnLeaveFullscreen(WebKit.WebView sender, EventArgs args)
	{
		try
		{
			if (!_htmlFullscreen)
				return;
			_htmlFullscreen = false;
			try { _window?.Unfullscreen(); } catch { /* ignore */ }
			_host?.ExitHtmlFullscreen();
			_overlay.ExitHtmlFullscreen();
			Console.WriteLine("[Browser] Linux HTML fullscreen leave");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux leave fullscreen failed: {ex.Message}");
		}
	}

	private string? PickSavePathBlocking(string suggestedFileName)
	{
		try
		{
			if (_window == null)
			{
				// No window — fall back to Downloads.
				var folder = BrowserDownloadService.GetDefaultDownloadsFolder();
				Directory.CreateDirectory(folder);
				return BrowserDownloadService.MakeUniquePath(Path.Combine(folder, suggestedFileName));
			}

			// Gtk4 FileDialog is async; run a nested main-context wait so decide-destination can stay sync.
			var dialog = Gtk.FileDialog.New();
			dialog.SetTitle("Save download");
			dialog.SetInitialName(suggestedFileName);

			string? resultPath = null;
			var done = false;
			Exception? error = null;

			dialog.Save(_window, cancellable: null, (obj, res) =>
			{
				try
				{
					var file = dialog.SaveFinish(res);
					resultPath = file?.GetPath();
				}
				catch (Exception ex)
				{
					// User cancel raises; treat as null.
					if (!ex.Message.Contains("Dismissed", StringComparison.OrdinalIgnoreCase)
					    && !ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
						error = ex;
				}
				finally
				{
					done = true;
				}
			});

			var ctx = GLib.MainContext.Default();
			while (!done)
				ctx.Iteration(mayBlock: true);

			if (error != null)
				Console.WriteLine($"[Browser] Linux save dialog error: {error.Message}");

			return resultPath;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux PickSavePath failed: {ex.Message}");
			return null;
		}
	}

	private static string PathToFileUri(string path)
	{
		var full = Path.GetFullPath(path);
		return new Uri(full).AbsoluteUri;
	}

	private static string? FileUriToPath(string? uri)
	{
		if (string.IsNullOrWhiteSpace(uri))
			return null;
		try
		{
			if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
				return new Uri(uri).LocalPath;
			return uri;
		}
		catch
		{
			return uri;
		}
	}
}
#endif
