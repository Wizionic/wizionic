using System.Text.Json;
using ChatfishApp.Core.Browser;
using Microsoft.Maui.Controls;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Platform hooks for the embedded browser WebView:
/// force all target=_blank / window.open into Chatfish tabs,
/// SPA location tracking, downloads, HTML fullscreen, clear-on-exit.
/// </summary>
public sealed class BrowserWebViewPlatformService
{
    private readonly IBrowserStore _store;
    private readonly MauiBrowserAgentService _agent;
    private readonly IBrowserDownloadService _downloads;
    private readonly BrowserOverlayService _overlay;
    private WebView? _webView;

#if WINDOWS
    private Microsoft.Web.WebView2.Core.CoreWebView2? _core;
    private Microsoft.UI.Xaml.Controls.WebView2? _wv2;
    private bool _scriptInstalled;
    private string? _lastOpenedUrl;
    private DateTime _lastOpenedUtc = DateTime.MinValue;
    /// <summary>Active WebView2 downloads keyed by our UI id (not COM object — RCWs are unreliable as dict keys).</summary>
    private readonly Dictionary<string, TrackedDownload> _trackedDownloads = new(StringComparer.Ordinal);
    private CancellationTokenSource? _downloadPollCts;
    private bool _appWasFullscreen;
    private Microsoft.UI.Windowing.AppWindowPresenterKind? _previousPresenterKind;

    private sealed class TrackedDownload
    {
        public required string Id { get; init; }
        public required Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation Operation { get; init; }
        public required string FilePath { get; init; }
        public Action? Unhook { get; set; }
        public bool Terminal { get; set; }
        public long LastFileBytes { get; set; }
        public DateTime LastFileGrowthUtc { get; set; } = DateTime.UtcNow;
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Runs before page scripts. Fully traps window.open + target=_blank so WebView2
    /// never creates an OS/Edge popup. Host opens a Chatfish tab via postMessage.
    /// </summary>
    private const string DocumentCreatedScript =
        """
        (() => {
          if (window.__chatfishBrowserHooks) return;
          window.__chatfishBrowserHooks = true;

          const post = (obj) => {
            try {
              if (window.chrome && chrome.webview && chrome.webview.postMessage) {
                chrome.webview.postMessage(JSON.stringify(obj));
                return true;
              }
            } catch (_) {}
            return false;
          };

          const resolveUrl = (raw) => {
            try {
              if (raw == null || raw === '') return '';
              return new URL(String(raw), location.href).href;
            } catch (_) {
              return String(raw || '');
            }
          };

          const isBlank = (u) => {
            if (!u) return true;
            const s = String(u);
            return s === 'about:blank' || s.startsWith('about:blank#') || s === 'about:srcdoc';
          };

          const openInChatfish = (raw) => {
            const href = resolveUrl(raw);
            if (!href || isBlank(href) || href.startsWith('javascript:')) return false;
            return post({ t: 'open', u: href });
          };

          const notifyLoc = (mode) => {
            try {
              post({ t: 'loc', u: location.href, title: document.title || '', mode: mode || 'push' });
            } catch (_) {}
          };

          // --- SPA location ---
          try {
            const origPush = history.pushState;
            history.pushState = function () {
              const r = origPush.apply(this, arguments);
              notifyLoc('push');
              return r;
            };
            const origReplace = history.replaceState;
            history.replaceState = function () {
              const r = origReplace.apply(this, arguments);
              notifyLoc('replace');
              return r;
            };
            window.addEventListener('popstate', () => notifyLoc('push'));
            window.addEventListener('hashchange', () => notifyLoc('push'));
          } catch (_) {}

          // --- Stub window for blank window.open() then w.location = url ---
          const makeStub = (initialHref) => {
            let href = initialHref || '';
            const loc = {
              get href() { return href; },
              set href(v) {
                href = String(v || '');
                if (!isBlank(href)) openInChatfish(href);
              },
              assign(v) { this.href = v; },
              replace(v) { this.href = v; },
              toString() { return href; }
            };
            return {
              closed: false,
              close() { this.closed = true; },
              focus() {},
              blur() {},
              get location() { return loc; },
              set location(v) {
                if (v && typeof v === 'object' && 'href' in v) loc.href = v.href;
                else loc.href = v;
              },
              document: {
                write() {},
                writeln() {},
                open() { return this; },
                close() {},
                get body() { return { appendChild() {}, innerHTML: '' }; }
              },
              opener: null
            };
          };

          // --- window.open: NEVER call the native implementation ---
          try {
            window.open = function (url, name, features) {
              const href = resolveUrl(url);
              if (href && !isBlank(href)) {
                openInChatfish(href);
                return makeStub(href);
              }
              // Blank open — return stub that captures later location writes.
              return makeStub('');
            };
          } catch (_) {}

          // --- target=_blank / _new: intercept every click in capture phase ---
          const onPossibleBlankNav = (e) => {
            try {
              if (e.defaultPrevented) return;
              if (e.type === 'click' && e.button !== 0) return;
              if (e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
              const path = typeof e.composedPath === 'function' ? e.composedPath() : [];
              let a = null;
              for (const n of path) {
                if (n && n.tagName === 'A') { a = n; break; }
              }
              if (!a && e.target && e.target.closest)
                a = e.target.closest('a[href]');
              if (!a || !a.href) return;

              const target = (a.getAttribute('target') || '').toLowerCase();
              // rel=noopener noreferrer does not change target; still _blank
              if (target !== '_blank' && target !== '_new') return;

              e.preventDefault();
              e.stopPropagation();
              if (typeof e.stopImmediatePropagation === 'function')
                e.stopImmediatePropagation();

              openInChatfish(a.href);
            } catch (_) {}
          };

          document.addEventListener('click', onPossibleBlankNav, true);
          document.addEventListener('auxclick', onPossibleBlankNav, true);

          // Rewrite dynamically inserted anchors so target never reaches the engine.
          const rewireAnchor = (a) => {
            try {
              if (!a || a.__chatfishRewired) return;
              const t = (a.getAttribute('target') || '').toLowerCase();
              if (t !== '_blank' && t !== '_new') return;
              a.__chatfishRewired = true;
              // Keep visual semantics but prevent WebView2 native popup path.
              a.setAttribute('data-chatfish-target', t);
              a.setAttribute('target', '_self');
              a.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                if (typeof e.stopImmediatePropagation === 'function')
                  e.stopImmediatePropagation();
                openInChatfish(a.href);
              }, true);
            } catch (_) {}
          };

          const rewireAll = (root) => {
            try {
              const scope = root && root.querySelectorAll ? root : document;
              scope.querySelectorAll('a[target="_blank"], a[target="_new"]').forEach(rewireAnchor);
            } catch (_) {}
          };

          const startObserver = () => {
            try {
              rewireAll(document);
              const mo = new MutationObserver((mutations) => {
                for (const m of mutations) {
                  if (m.type === 'attributes' && m.target && m.target.tagName === 'A')
                    rewireAnchor(m.target);
                  m.addedNodes && m.addedNodes.forEach((n) => {
                    if (n.nodeType !== 1) return;
                    if (n.tagName === 'A') rewireAnchor(n);
                    else rewireAll(n);
                  });
                }
              });
              mo.observe(document.documentElement || document, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['target', 'href']
              });
            } catch (_) {}
          };

          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', startObserver, { once: true });
          else
            startObserver();
        })();
        """;
#endif

    public BrowserWebViewPlatformService(
        IBrowserStore store,
        MauiBrowserAgentService agent,
        IBrowserDownloadService downloads,
        BrowserOverlayService overlay)
    {
        _store = store;
        _agent = agent;
        _downloads = downloads;
        _overlay = overlay;
    }

    public void Attach(WebView webView)
    {
        _webView = webView;
        webView.HandlerChanged += OnHandlerChanged;
        OnHandlerChanged(webView, EventArgs.Empty);
    }

    /// <summary>Optional; popup-capture WebView is no longer required (kept for API compatibility).</summary>
    public void AttachPopupCapture(WebView captureWebView)
    {
        // Intentionally unused: assigning NewWindow to a second WebView was unreliable and
        // could surface as an extra "browser window". All popups are trapped in-script + Handled.
        _ = captureWebView;
    }

    public async Task ApplyClearOnExitAsync(CancellationToken ct = default)
    {
        var settings = _store.GetSettings();

#if WINDOWS
        if (_core?.Profile != null && (settings.ClearCookiesOnExit || settings.ClearCacheOnExit))
        {
            try
            {
                Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds dataKinds = 0;
                if (settings.ClearCookiesOnExit)
                    dataKinds |= Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.Cookies;
                if (settings.ClearCacheOnExit)
                    dataKinds |= Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.DiskCache
                        | Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.DownloadHistory;

                if (dataKinds != 0)
                    await _core.Profile.ClearBrowsingDataAsync(dataKinds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Browser] clear browsing data failed: {ex.Message}");
            }
        }
#endif

        if (settings.ClearHistoryOnExit)
            await _store.ClearHistoryAsync(ct);
    }

    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (_webView?.Handler?.PlatformView == null)
            return;

#if WINDOWS
        if (_webView.Handler.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv2)
        {
            _wv2 = wv2;
            // Core can be recreated; always (re)configure when the platform view is ready.
            _ = ConfigureWindowsAsync(wv2);
        }
#endif
    }

#if WINDOWS
    private async Task ConfigureWindowsAsync(Microsoft.UI.Xaml.Controls.WebView2 wv2)
    {
        try
        {
            await wv2.EnsureCoreWebView2Async();
            var core = wv2.CoreWebView2;
            if (core == null)
            {
                Console.WriteLine("[Browser] CoreWebView2 null after Ensure");
                return;
            }

            if (!ReferenceEquals(_core, core))
            {
                if (_core != null)
                    UnhookCore(_core);
                _core = core;
                HookCore(core);
                _scriptInstalled = false;
            }

            if (!_scriptInstalled)
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(DocumentCreatedScript);
                _scriptInstalled = true;
            }

            Console.WriteLine("[Browser] WebView2 hooks ready (hard trap for target=_blank / window.open)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] WebView2 configure failed: {ex.Message}");
        }
    }

    private void HookCore(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        // Synchronous handler — set Handled before anything else can create a popup.
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.HistoryChanged += OnHistoryChanged;
        core.SourceChanged += OnSourceChanged;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
    }

    private void UnhookCore(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        core.NewWindowRequested -= OnNewWindowRequested;
        core.DownloadStarting -= OnDownloadStarting;
        core.HistoryChanged -= OnHistoryChanged;
        core.SourceChanged -= OnSourceChanged;
        core.DocumentTitleChanged -= OnDocumentTitleChanged;
        core.WebMessageReceived -= OnWebMessageReceived;
        core.NavigationCompleted -= OnNavigationCompleted;
        core.ContainsFullScreenElementChanged -= OnContainsFullScreenElementChanged;
    }

    /// <summary>
    /// Must stay synchronous. Never assign args.NewWindow (that can surface another window).
    /// Handled=true alone cancels the engine popup; we open a Chatfish tab when Uri is known.
    /// </summary>
    private void OnNewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;

        try
        {
            var target = (args.Uri ?? "").Trim();
            Console.WriteLine($"[Browser] NewWindowRequested (handled) uri='{target}'");

            if (!IsBlankUri(target))
                OpenUrlInAppOrExternalDebounced(target);
            // about:blank: page may use our JS stub for the real URL; do not create a window.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] NewWindowRequested failed: {ex.Message}");
        }
    }

    private void OpenUrlInAppOrExternal(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || IsBlankUri(url))
            return;

        // Always keep the user inside Chatfish for new-window / target=_blank requests.
        // (Browser settings "External browser" still applies to the explicit toolbar button.)
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Console.WriteLine($"[Browser] open in Chatfish tab: {url}");
            _ = _agent.OpenInNewTabAsync(url);
        });
    }

    private static bool IsBlankUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return true;
        var u = uri.Trim();
        return u.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
               || u.StartsWith("about:blank#", StringComparison.OrdinalIgnoreCase)
               || u.Equals("about:srcdoc", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDownloadStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2DownloadStartingEventArgs args)
    {
        // Defer so we can show a save dialog (or pick a default path) without blocking the WebView thread.
        var deferral = args.GetDeferral();
        var op = args.DownloadOperation;
        var suggestedPath = args.ResultFilePath ?? "";
        var suggestedName = Path.GetFileName(suggestedPath);
        if (string.IsNullOrWhiteSpace(suggestedName))
            suggestedName = "download";

        // Capture URI now — some properties can be flaky later.
        var sourceUri = "";
        try { sourceUri = op.Uri ?? ""; } catch { /* ignore */ }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                string? destPath;
                if (_store.GetSettings().AskBeforeDownloading)
                {
                    destPath = await PickSaveFilePathAsync(suggestedName);
                    if (string.IsNullOrWhiteSpace(destPath))
                    {
                        args.Cancel = true;
                        Console.WriteLine($"[Browser] download cancelled by user: {sourceUri}");
                        return;
                    }
                }
                else
                {
                    var folder = BrowserDownloadService.GetDefaultDownloadsFolder();
                    Directory.CreateDirectory(folder);
                    destPath = BrowserDownloadService.MakeUniquePath(Path.Combine(folder, suggestedName));
                }

                args.ResultFilePath = destPath;
                // Hide WebView2's default download UI — we surface progress in the Chatfish toolbar.
                args.Handled = true;

                var item = _downloads.Begin(sourceUri, destPath, Path.GetFileName(destPath));
                TrackDownloadOperation(item.Id, op, destPath);
                Console.WriteLine($"[Browser] download started id={item.Id} -> {destPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Browser] download start failed: {ex.Message}");
                try { args.Cancel = true; } catch { /* ignore */ }
            }
            finally
            {
                try { deferral.Complete(); } catch { /* ignore */ }
            }
        });
    }

    /// <summary>
    /// Subscribe with id-capturing lambdas (avoids COM-as-dictionary-key issues) and
    /// start a poll loop — StateChanged is known to miss for some files / WebView2 builds.
    /// </summary>
    private void TrackDownloadOperation(
        string id,
        Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation op,
        string filePath)
    {
        var tracked = new TrackedDownload
        {
            Id = id,
            Operation = op,
            FilePath = filePath
        };

        // Local functions capture tracked by reference — no COM-object dictionary lookup.
        void OnState(object? s, object e) => ApplyDownloadState(tracked);
        void OnBytes(object? s, object e) => ApplyDownloadProgress(tracked);

        try
        {
            op.StateChanged += OnState;
            op.BytesReceivedChanged += OnBytes;
            tracked.Unhook = () =>
            {
                try
                {
                    op.StateChanged -= OnState;
                    op.BytesReceivedChanged -= OnBytes;
                }
                catch { /* ignore */ }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] download event subscribe failed: {ex.Message}");
        }

        lock (_trackedDownloads)
            _trackedDownloads[id] = tracked;

        // Apply current state immediately (may already be completed for tiny files).
        ApplyDownloadState(tracked);
        ApplyDownloadProgress(tracked);

        EnsureDownloadPollRunning();
    }

    private void ApplyDownloadProgress(TrackedDownload tracked)
    {
        if (tracked.Terminal)
            return;

        try
        {
            var op = tracked.Operation;
            var bytes = SafeGetBytesReceived(op);
            var total = SafeGetTotalBytes(op);

            _downloads.Update(tracked.Id, item =>
            {
                if (bytes >= 0)
                    item.BytesReceived = bytes;
                if (total > 0)
                    item.TotalBytes = total;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] download progress update failed: {ex.Message}");
        }
    }

    private void ApplyDownloadState(TrackedDownload tracked)
    {
        if (tracked.Terminal)
            return;

        try
        {
            var op = tracked.Operation;
            var state = op.State;
            Console.WriteLine($"[Browser] download state id={tracked.Id} state={state} bytes={SafeGetBytesReceived(op)}");

            switch (state)
            {
                case Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.InProgress:
                    ApplyDownloadProgress(tracked);
                    break;

                case Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Completed:
                    FinishTracked(tracked, success: true, path: SafeGetResultPath(op) ?? tracked.FilePath, error: null);
                    break;

                case Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Interrupted:
                    var reason = "Interrupted";
                    try { reason = op.InterruptReason.ToString(); } catch { /* ignore */ }
                    FinishTracked(tracked, success: false, path: null, error: reason);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] download state update failed: {ex.Message}");
        }
    }

    private void FinishTracked(TrackedDownload tracked, bool success, string? path, string? error)
    {
        if (tracked.Terminal)
            return;
        tracked.Terminal = true;

        try { tracked.Unhook?.Invoke(); } catch { /* ignore */ }
        tracked.Unhook = null;

        lock (_trackedDownloads)
            _trackedDownloads.Remove(tracked.Id);

        if (success)
        {
            var finalPath = !string.IsNullOrWhiteSpace(path) ? path : tracked.FilePath;
            // Prefer on-disk size when WebView2 didn't report bytes.
            long fileBytes = 0;
            try
            {
                if (File.Exists(finalPath))
                    fileBytes = new FileInfo(finalPath).Length;
            }
            catch { /* ignore */ }

            if (fileBytes > 0)
            {
                _downloads.Update(tracked.Id, item =>
                {
                    item.BytesReceived = fileBytes;
                    item.TotalBytes = fileBytes;
                });
            }

            _downloads.Complete(tracked.Id, finalPath);
            Console.WriteLine($"[Browser] download completed id={tracked.Id} path={finalPath}");
        }
        else
        {
            _downloads.Fail(tracked.Id, error);
            Console.WriteLine($"[Browser] download failed id={tracked.Id}: {error}");
        }
    }

    private void EnsureDownloadPollRunning()
    {
        lock (_trackedDownloads)
        {
            if (_downloadPollCts != null)
                return;
            if (_trackedDownloads.Count == 0)
                return;
            _downloadPollCts = new CancellationTokenSource();
        }

        var cts = _downloadPollCts!;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(400, cts.Token).ConfigureAwait(false);
                    List<TrackedDownload> snapshot;
                    lock (_trackedDownloads)
                        snapshot = _trackedDownloads.Values.Where(t => !t.Terminal).ToList();

                    if (snapshot.Count == 0)
                        break;

                    foreach (var t in snapshot)
                    {
                        try
                        {
                            // StateChanged / BytesReceivedChanged are flaky on several WebView2 runtimes
                            // (state can stick at InProgress forever even when bytes == total).
                            ApplyDownloadState(t);
                            if (t.Terminal)
                                continue;

                            ApplyDownloadProgress(t);
                            if (TryCompleteFromWebViewOrFile(t))
                                continue;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Browser] download poll error: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            finally
            {
                var restart = false;
                lock (_trackedDownloads)
                {
                    if (ReferenceEquals(_downloadPollCts, cts))
                    {
                        _downloadPollCts.Dispose();
                        _downloadPollCts = null;
                    }
                    restart = _trackedDownloads.Count > 0;
                }
                // Restart outside the lock if more downloads are still active.
                if (restart)
                    EnsureDownloadPollRunning();
            }
        }, cts.Token);
    }

    /// <summary>
    /// Completes a download using State when available, otherwise heuristics:
    /// bytesReceived == total, or on-disk file size stable after growth
    /// (WebView2 bug: State stays InProgress and StateChanged never fires).
    /// </summary>
    private bool TryCompleteFromWebViewOrFile(TrackedDownload t)
    {
        if (t.Terminal)
            return true;

        try
        {
            var state = t.Operation.State;
            if (state == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Completed)
            {
                FinishTracked(t, success: true, path: SafeGetResultPath(t.Operation) ?? t.FilePath, error: null);
                return true;
            }

            if (state == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Interrupted)
            {
                var reason = "Interrupted";
                try { reason = t.Operation.InterruptReason.ToString(); } catch { /* ignore */ }
                FinishTracked(t, success: false, path: null, error: reason);
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] download state read failed: {ex.Message}");
        }

        var bytes = SafeGetBytesReceived(t.Operation);
        var total = SafeGetTotalBytes(t.Operation);
        var fileBytes = SafeGetFileLength(t.FilePath);

        // Keep UI progress moving even when BytesReceivedChanged never fires.
        if (fileBytes > 0 || bytes > 0)
        {
            var reported = Math.Max(fileBytes, bytes > 0 ? bytes : 0);
            _downloads.Update(t.Id, item =>
            {
                if (reported > item.BytesReceived)
                    item.BytesReceived = reported;
                if (total > 0)
                    item.TotalBytes = total;
                else if (fileBytes > 0 && item.TotalBytes is null or 0)
                {
                    // Unknown total — leave TotalBytes unset so UI shows absolute bytes only.
                }
            });
        }

        // Known WebView2 bug: bytesReceived == totalBytes but State still InProgress forever.
        if (total > 0 && (bytes >= total || fileBytes >= total))
        {
            Console.WriteLine($"[Browser] download heuristic complete (bytes/total) id={t.Id} bytes={bytes} file={fileBytes} total={total}");
            FinishTracked(t, success: true, path: t.FilePath, error: null);
            return true;
        }

        // Track on-disk growth; complete when the file stops growing for a short window.
        // FileSavePicker creates a 0-byte placeholder — only count growth after bytes appear.
        if (fileBytes > t.LastFileBytes)
        {
            t.LastFileBytes = fileBytes;
            t.LastFileGrowthUtc = DateTime.UtcNow;
        }
        else if (fileBytes > 0)
        {
            var stableFor = DateTime.UtcNow - t.LastFileGrowthUtc;
            var runningFor = DateTime.UtcNow - t.StartedUtc;
            // Unknown Content-Length path: treat as done once the file stops growing.
            // 2.5s stability avoids false completes during slow TCP stalls.
            if (runningFor.TotalSeconds >= 1.5 && stableFor.TotalMilliseconds >= 2500)
            {
                Console.WriteLine($"[Browser] download heuristic complete (file stable) id={t.Id} file={fileBytes} stableMs={stableFor.TotalMilliseconds:F0}");
                FinishTracked(t, success: true, path: t.FilePath, error: null);
                return true;
            }
        }

        return false;
    }

    private static long SafeGetBytesReceived(Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation op)
    {
        try { return op.BytesReceived; }
        catch { return -1; }
    }

    private static long SafeGetTotalBytes(Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation op)
    {
        try
        {
            var total = op.TotalBytesToReceive;
            // API is long; unknown may be 0 or negative.
            return total > 0 ? total : 0;
        }
        catch { return 0; }
    }

    private static string? SafeGetResultPath(Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation op)
    {
        try
        {
            var path = op.ResultFilePath;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch { return null; }
    }

    private static long SafeGetFileLength(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return 0;
            return new FileInfo(path).Length;
        }
        catch { return 0; }
    }

    private static async Task<string?> PickSaveFilePathAsync(string suggestedFileName)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "download" : suggestedFileName;

            var ext = Path.GetExtension(suggestedFileName);
            if (!string.IsNullOrWhiteSpace(ext))
            {
                var label = ext.TrimStart('.').ToUpperInvariant() + " file";
                picker.FileTypeChoices.Add(label, new List<string> { ext });
            }
            picker.FileTypeChoices.Add("All files", new List<string> { "." });

            // Associate with the MAUI WinUI window so the picker is modal to the app.
            var hwnd = GetMainWindowHandle();
            if (hwnd != IntPtr.Zero)
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] save picker failed: {ex.Message}");
            return null;
        }
    }

    private static IntPtr GetMainWindowHandle()
    {
        try
        {
            var window = Microsoft.Maui.Controls.Application.Current?.Windows?.FirstOrDefault();
            if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winui)
                return WinRT.Interop.WindowNative.GetWindowHandle(winui);
        }
        catch { /* ignore */ }
        return IntPtr.Zero;
    }

    private void OnContainsFullScreenElementChanged(object? sender, object e)
    {
        if (_core == null)
            return;

        var enter = _core.ContainsFullScreenElement;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (enter)
                    EnterAppFullscreen();
                else
                    ExitAppFullscreen();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Browser] fullscreen toggle failed: {ex.Message}");
            }
        });
    }

    private void EnterAppFullscreen()
    {
        _overlay.EnterHtmlFullscreen();
        try
        {
            var appWindow = TryGetAppWindow();
            if (appWindow != null)
            {
                _previousPresenterKind = appWindow.Presenter?.Kind
                    ?? Microsoft.UI.Windowing.AppWindowPresenterKind.Default;
                if (appWindow.Presenter?.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
                {
                    appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
                    _appWasFullscreen = true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] AppWindow fullscreen failed: {ex.Message}");
        }

        Console.WriteLine("[Browser] entered HTML/app fullscreen");
    }

    private void ExitAppFullscreen()
    {
        try
        {
            if (_appWasFullscreen)
            {
                var appWindow = TryGetAppWindow();
                if (appWindow != null)
                {
                    var restore = _previousPresenterKind
                        ?? Microsoft.UI.Windowing.AppWindowPresenterKind.Default;
                    if (restore == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
                        restore = Microsoft.UI.Windowing.AppWindowPresenterKind.Default;
                    appWindow.SetPresenter(restore);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] AppWindow exit fullscreen failed: {ex.Message}");
        }
        finally
        {
            _appWasFullscreen = false;
            _previousPresenterKind = null;
            _overlay.ExitHtmlFullscreen();
        }

        Console.WriteLine("[Browser] exited HTML/app fullscreen");
    }

    private static Microsoft.UI.Windowing.AppWindow? TryGetAppWindow()
    {
        try
        {
            var hwnd = GetMainWindowHandle();
            if (hwnd == IntPtr.Zero)
                return null;
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        }
        catch
        {
            return null;
        }
    }

    private void OnHistoryChanged(object? sender, object e) =>
        SyncLocationFromCore(pushHistory: true);

    private void OnSourceChanged(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2SourceChangedEventArgs e) =>
        SyncLocationFromCore(pushHistory: !e.IsNewDocument);

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        if (_core == null)
            return;
        var title = _core.DocumentTitle;
        MainThread.BeginInvokeOnMainThread(() => _agent.NotifyDocumentTitleChanged(title));
    }

    private void OnNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) =>
        SyncLocationFromCore(pushHistory: false);

    private void SyncLocationFromCore(bool pushHistory)
    {
        if (_core == null)
            return;

        var url = _core.Source;
        if (string.IsNullOrWhiteSpace(url))
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _agent.NotifyClientLocationChanged(url, pushHistory);
            if (!string.IsNullOrWhiteSpace(_core?.DocumentTitle))
                _agent.NotifyDocumentTitleChanged(_core!.DocumentTitle);
        });
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? raw = null;
        try
        {
            raw = e.TryGetWebMessageAsString();
        }
        catch
        {
            try { raw = e.WebMessageAsJson; } catch { /* ignore */ }
        }

        if (string.IsNullOrWhiteSpace(raw))
            return;

        raw = raw.Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try { raw = JsonSerializer.Deserialize<string>(raw) ?? raw; } catch { /* keep */ }
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var typeProp))
                return;

            var type = typeProp.GetString();
            if (type == "loc")
            {
                var url = root.TryGetProperty("u", out var u) ? u.GetString() : null;
                var mode = root.TryGetProperty("mode", out var m) ? m.GetString() : "push";
                var title = root.TryGetProperty("title", out var ti) ? ti.GetString() : null;
                if (string.IsNullOrWhiteSpace(url))
                    return;

                var push = !string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _agent.NotifyClientLocationChanged(url!, push);
                    if (!string.IsNullOrWhiteSpace(title))
                        _agent.NotifyDocumentTitleChanged(title);
                });
            }
            else if (type == "open")
            {
                var url = root.TryGetProperty("u", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url) || IsBlankUri(url))
                    return;

                OpenUrlInAppOrExternalDebounced(url!);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] web message parse failed: {ex.Message} raw={raw}");
        }
    }

    private void OpenUrlInAppOrExternalDebounced(string url)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(_lastOpenedUrl, url, StringComparison.OrdinalIgnoreCase)
            && (now - _lastOpenedUtc).TotalMilliseconds < 750)
        {
            Console.WriteLine($"[Browser] skip duplicate open: {url}");
            return;
        }

        _lastOpenedUrl = url;
        _lastOpenedUtc = now;
        OpenUrlInAppOrExternal(url);
    }
#endif
}
