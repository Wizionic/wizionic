using System.Text.Json;
using ChatfishApp.Core.Browser;
using Microsoft.Maui.Controls;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Platform hooks for the embedded browser WebView:
/// force all target=_blank / window.open into Chatfish tabs,
/// SPA location tracking, downloads, clear-on-exit.
/// </summary>
public sealed class BrowserWebViewPlatformService
{
    private readonly IBrowserStore _store;
    private readonly MauiBrowserAgentService _agent;
    private WebView? _webView;

#if WINDOWS
    private Microsoft.Web.WebView2.Core.CoreWebView2? _core;
    private Microsoft.UI.Xaml.Controls.WebView2? _wv2;
    private bool _scriptInstalled;
    private string? _lastOpenedUrl;
    private DateTime _lastOpenedUtc = DateTime.MinValue;

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

    public BrowserWebViewPlatformService(IBrowserStore store, MauiBrowserAgentService agent)
    {
        _store = store;
        _agent = agent;
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
        if (_store.GetSettings().AskBeforeDownloading)
        {
            args.Handled = true;
            args.Cancel = true;
            Console.WriteLine($"[Browser] download blocked (ask first): {args.DownloadOperation.Uri}");
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
