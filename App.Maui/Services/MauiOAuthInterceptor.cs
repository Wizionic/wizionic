using System.Net.Http.Json;
using App.Core.Browser;
using App.Core.Connectors;
using App.Core.Storage;
using App.Core.Sync;
using App.Core.UI;
using App.Shared.Services;

namespace App.Maui.Services;

/// <summary>
/// Completes OAuth inside the embedded browser: watches navigations for
/// <c>/api/oauth/done?oauth_session=…</c> (or custom scheme), redeems the one-shot
/// session into <see cref="IKeyStore"/>, and notifies the Tools UI.
/// </summary>
public sealed class MauiOAuthInterceptor : IDisposable
{
    private readonly IBrowserAgentService _browser;
    private readonly IBrowserTabManager _tabs;
    private readonly IBrowserPanelState _panel;
    private readonly IChatPanelState _chat;
    private readonly INotesPanelState? _notes;
    private readonly IAppNavigation _nav;
    private readonly OAuthReturnBridge _bridge;
    private readonly IKeyStore _keys;
    private readonly HttpClient _http;
    private readonly IOpenApiConnectorRefresher? _openApi;
    private readonly ISettingsSyncStore? _settingsSync;
    private readonly ISyncService? _sync;
    private readonly object _gate = new();
    private string? _lastHandledSession;
    private bool _subscribed;
    private bool _oauthFlowActive;

    public MauiOAuthInterceptor(
        IBrowserAgentService browser,
        IBrowserTabManager tabs,
        IBrowserPanelState panel,
        IChatPanelState chat,
        IAppNavigation nav,
        OAuthReturnBridge bridge,
        IKeyStore keys,
        HttpClient http,
        IOpenApiConnectorRefresher? openApi = null,
        ISettingsSyncStore? settingsSync = null,
        ISyncService? sync = null,
        INotesPanelState? notes = null)
    {
        _browser = browser;
        _tabs = tabs;
        _panel = panel;
        _chat = chat;
        _notes = notes;
        _nav = nav;
        _bridge = bridge;
        _keys = keys;
        _http = http;
        _openApi = openApi;
        _settingsSync = settingsSync;
        _sync = sync;
        EnsureSubscribed();
    }

    public void EnsureSubscribed()
    {
        if (_subscribed) return;
        _browser.UrlChanged += OnUrlChanged;
        _subscribed = true;
    }

    /// <summary>
    /// Open OAuth in the in-app browser. Embedded browser only mounts on /chat,
    /// so we navigate there and show the browser panel (same as top-bar browser toggle).
    /// </summary>
    public async Task OpenInAppBrowserAsync(string url, CancellationToken ct = default)
    {
        EnsureSubscribed();
        _oauthFlowActive = true;

        // Mirror AppTopBar.ToggleBrowser when leaving Tools/Settings:
        // browser pane only, on Chat route, chat history closed so the WebView is front-and-center.
        if (_notes is not null)
            _notes.IsOpen = false;
        _chat.IsOpen = false;
        _panel.IsOpen = true;

        if (!_nav.IsPath("/chat"))
            _nav.NavigateTo("/chat");

        // Wait for Chat page + EmbeddedBrowser to mount and WebView to become available.
        for (var i = 0; i < 40 && !_browser.IsAvailable; i++)
            await Task.Delay(50, ct);

        // One more frame for overlay layout after panel open.
        await Task.Delay(100, ct);

        try
        {
            if (_browser.IsAvailable)
                await _tabs.OpenInNewTabAsync(url, ct);
            else
                await _browser.NavigateAsync(url, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OAuth] Navigate failed: {ex.Message}");
            _oauthFlowActive = false;
            throw;
        }
    }

    private void OnUrlChanged(string url) =>
        _ = HandleUrlAsync(url);

    private async Task HandleUrlAsync(string url)
    {
        if (!TryExtractOAuthQuery(url, out var query, out var sessionId))
            return;

        lock (_gate)
        {
            if (string.Equals(_lastHandledSession, sessionId, StringComparison.Ordinal))
                return;
            _lastHandledSession = sessionId;
        }

        Console.WriteLine($"[OAuth] Intercepted return URL session={sessionId}");

        try
        {
            if (query.Contains("oauth_error=", StringComparison.OrdinalIgnoreCase))
            {
                var err = ParseQuery(query).GetValueOrDefault("oauth_error") ?? "OAuth failed";
                FinishOAuthFlow(Uri.UnescapeDataString(err), isError: true);
                return;
            }

            // Redeem here (one-shot session). Tools page only shows status — do not SetFromQuery
            // or Tools would race and consume the session first.
            var session = await _http.GetFromJsonAsync<OAuthSessionDto>(
                $"/api/oauth/session/{Uri.EscapeDataString(sessionId)}");
            if (session is null || string.IsNullOrWhiteSpace(session.AccessToken))
            {
                FinishOAuthFlow("OAuth session expired. Please try connecting again.", isError: true);
                return;
            }

            var connectorId = !string.IsNullOrWhiteSpace(session.ConnectorId)
                ? session.ConnectorId
                : ParseQuery(query).GetValueOrDefault("oauth_connector") ?? "";

            var tokens = new OAuthTokenSet(
                session.AccessToken,
                session.RefreshToken,
                session.ExpiresAtUtc,
                session.TokenType,
                session.Scope,
                session.AccountLabel);

            await _keys.UpsertOAuthConnectorAsync(new OAuthConnectorInstall(
                connectorId,
                Enabled: true,
                Tokens: tokens,
                ConnectedAtUtc: DateTimeOffset.UtcNow,
                AccountLabel: session.AccountLabel));

            await SettingsSyncHooks.AfterLocalSaveAsync(_settingsSync, _sync, SettingsSyncCategory.Tools);
            if (_openApi is not null)
                await _openApi.RefreshFromKeyStoreAsync();

            var label = ConnectorCatalogName(connectorId);
            var who = string.IsNullOrWhiteSpace(session.AccountLabel) ? "" : $" ({session.AccountLabel})";
            FinishOAuthFlow($"Connected {label}{who}", isError: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OAuth] Complete failed: {ex.Message}");
            FinishOAuthFlow($"Could not complete OAuth: {ex.Message}", isError: true);
        }
    }

    /// <summary>Close browser pane, return to Tools, surface status banner.</summary>
    private void FinishOAuthFlow(string message, bool isError)
    {
        _oauthFlowActive = false;

        void Apply()
        {
            try
            {
                _ = _browser.NavigateAsync("about:blank");
            }
            catch
            {
                // ignore
            }

            _panel.IsOpen = false;
            // Set status before navigate so Tools OnInitialized can TakeStatus if it mounts after.
            _bridge.SetStatus(message, isError);
            _nav.NavigateTo("/tools");
        }

        // WebView / Blazor navigation must happen on the UI thread.
        if (MainThread.IsMainThread)
            Apply();
        else
            MainThread.BeginInvokeOnMainThread(Apply);
    }

    private static string ConnectorCatalogName(string id) => id.ToLowerInvariant() switch
    {
        "gmail" => "Gmail",
        "google-calendar" => "Google Calendar",
        "github" => "GitHub",
        "notion" => "Notion",
        "stripe" => "Stripe",
        _ => id
    };

    /// <summary>
    /// Match https://host/api/oauth/done?oauth_session=… or wizionic://oauth?oauth_session=…
    /// or any URL that carries oauth_session (e.g. /tools).
    /// </summary>
    public static bool TryExtractOAuthQuery(string url, out string query, out string sessionId)
    {
        query = "";
        sessionId = "";
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var q = uri.Query;
        if (string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(uri.Fragment))
            q = uri.Fragment.StartsWith('#') ? "?" + uri.Fragment[1..] : "?" + uri.Fragment;
        if (string.IsNullOrEmpty(q))
            return false;

        var parsed = ParseQuery(q);
        if (!parsed.TryGetValue("oauth_session", out var sid) || string.IsNullOrWhiteSpace(sid))
        {
            if (!parsed.TryGetValue("oauth_error", out _))
                return false;
        }

        // Prefer known completion paths to avoid false positives.
        var path = uri.AbsolutePath ?? "";
        var isDone = path.Contains("/api/oauth/done", StringComparison.OrdinalIgnoreCase)
                     || path.Contains("/tools", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(uri.Scheme, "wizionic", StringComparison.OrdinalIgnoreCase);
        if (!isDone && !parsed.ContainsKey("oauth_session"))
            return false;
        if (!isDone && parsed.ContainsKey("oauth_session"))
        {
            // Still accept oauth_session on our host callback-ish paths.
            if (!path.Contains("oauth", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("tools", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        query = q.StartsWith('?') ? q[1..] : q;
        sessionId = sid ?? "";
        return parsed.ContainsKey("oauth_session") || parsed.ContainsKey("oauth_error");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return dict;
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            dict[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return dict;
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        _browser.UrlChanged -= OnUrlChanged;
        _subscribed = false;
    }

    private sealed class OAuthSessionDto
    {
        public string ConnectorId { get; set; } = "";
        public string Provider { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string? RefreshToken { get; set; }
        public DateTimeOffset? ExpiresAtUtc { get; set; }
        public string? TokenType { get; set; }
        public string? Scope { get; set; }
        public string? AccountLabel { get; set; }
    }
}
