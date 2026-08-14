using System.Runtime.InteropServices;
using System.Text.Json;
using App.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace App.Maui.Services;

/// <summary>
/// Mutable MAUI login-server URL: persists to appsettings.Local.json and applies live to HttpClient + cookies.
/// </summary>
public sealed class MauiAppServerEndpoint : IAppServerEndpoint
{
    private readonly HttpClient _http;
    private readonly MauiAuthCookieStore _cookieStore;
    private readonly ILogger<MauiAppServerEndpoint> _logger;
    private string _baseUrl;
    private string? _updateFeedUrlOverride;

    public MauiAppServerEndpoint(
        IOptions<AppServerOptions> options,
        HttpClient http,
        MauiAuthCookieStore cookieStore,
        ILogger<MauiAppServerEndpoint> logger)
    {
        var opts = options.Value;
        _baseUrl = NormalizeBaseUrl(opts.BaseUrl);
        _updateFeedUrlOverride = NormalizeFeedOverride(opts.UpdateFeedUrl);
        _http = http;
        _cookieStore = cookieStore;
        _logger = logger;

        // Self-heal persisted misconfiguration (e.g. Linux install stuck on /releases/windows
        // after an older homeserver retarget). Correct it and rewrite appsettings.Local.json.
        if (NeedsFeedHeal(opts.UpdateFeedUrl, _updateFeedUrlOverride))
        {
            try
            {
                PersistSync();
                _logger.LogWarning(
                    "[ServerEndpoint] Corrected UpdateFeedUrl from {Old} to {New}",
                    opts.UpdateFeedUrl, _updateFeedUrlOverride ?? "(derived)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ServerEndpoint] Could not persist corrected UpdateFeedUrl");
            }
        }

        ApplyLive();
    }

    public string BaseUrl => _baseUrl;

    public string UpdateFeedUrl =>
        !string.IsNullOrWhiteSpace(_updateFeedUrlOverride)
            ? _updateFeedUrlOverride!
            : ResolveDefaultFeedUrl(_baseUrl);

    public string SyncHubUrl => new Uri(BaseUri, "sync-hub").ToString();

    public Uri BaseUri => new(_baseUrl.TrimEnd('/') + "/");

    public event Action? OnChanged;

    public async Task<bool> SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Login server URL is required.", nameof(baseUrl));

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Enter a valid http(s) URL.", nameof(baseUrl));

        normalized = normalized.TrimEnd('/');
        var unchanged = string.Equals(_baseUrl, normalized, StringComparison.OrdinalIgnoreCase);
        _baseUrl = normalized;

        // When pointing at a local homeserver, keep Velopack updates on the public
        // production feed for this OS. Always re-assert the platform-correct path so an
        // older Windows-only override cannot stick on Linux (or vice versa).
        if (IsLocalHost(_baseUrl))
            _updateFeedUrlOverride = PublicProductionFeedUrl();
        else
            // Non-local: clear override so feed derives from BaseUrl + platform path,
            // unless an explicit override was already platform-correct.
            _updateFeedUrlOverride = NormalizeFeedOverride(_updateFeedUrlOverride);

        await PersistAsync(cancellationToken);
        var appliedLive = ApplyLive();
        OnChanged?.Invoke();
        _logger.LogInformation(
            "[ServerEndpoint] Login server set to {BaseUrl} (update feed {Feed}, live={Live})",
            _baseUrl, UpdateFeedUrl, appliedLive);

        // HttpClient cannot change BaseAddress after the first request; SignalR/cookies
        // also stay on the old host. Restart whenever the URL actually changed.
        return !unchanged;
    }

    /// <returns>False when HttpClient already sent a request (BaseAddress is frozen).</returns>
    private bool ApplyLive()
    {
        try
        {
            _http.BaseAddress = BaseUri;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation(
                ex,
                "[ServerEndpoint] HttpClient already in use; new URL is persisted, restart required");
            try
            {
                _cookieStore.Configure(new AppServerOptions
                {
                    BaseUrl = _baseUrl,
                    UpdateFeedUrl = _updateFeedUrlOverride
                });
            }
            catch
            {
                // cookie retarget is best-effort when restart is coming
            }
            return false;
        }

        _cookieStore.Configure(new AppServerOptions
        {
            BaseUrl = _baseUrl,
            UpdateFeedUrl = _updateFeedUrlOverride
        });
        return true;
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var path = Path.Combine(MauiAppData.Directory, "appsettings.Local.json");
        Directory.CreateDirectory(MauiAppData.Directory);
        var json = SerializeLocalSettings();
        await File.WriteAllTextAsync(path, json, ct);
    }

    private void PersistSync()
    {
        var path = Path.Combine(MauiAppData.Directory, "appsettings.Local.json");
        Directory.CreateDirectory(MauiAppData.Directory);
        File.WriteAllText(path, SerializeLocalSettings());
    }

    private string SerializeLocalSettings()
    {
        var payload = new
        {
            AppServer = new
            {
                BaseUrl = _baseUrl,
                UpdateFeedUrl = _updateFeedUrlOverride
            }
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Default Velopack feed: public production channel when login server is local;
    /// otherwise {BaseUrl}/releases/{linux|windows}.
    /// </summary>
    internal static string ResolveDefaultFeedUrl(string baseUrl)
    {
        if (IsLocalHost(baseUrl))
            return PublicProductionFeedUrl();

        return baseUrl.TrimEnd('/') + "/" + AppServerOptions.DefaultUpdateFeedPath;
    }

    /// <summary>Public wizionic.com feed for the current OS (never cross-platform).</summary>
    internal static string PublicProductionFeedUrl() =>
        IsLinuxDesktop()
            ? "https://wizionic.com/releases/linux"
            : "https://wizionic.com/releases/windows";

    /// <summary>
    /// Normalize / correct a stored feed override. Rewrites a cross-platform path
    /// (linux app with /releases/windows, or windows app with /releases/linux) to the
    /// matching channel on the same host when possible.
    /// </summary>
    internal static string? NormalizeFeedOverride(string? feedUrl)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
            return null;

        var trimmed = feedUrl.Trim().TrimEnd('/');
        var linux = IsLinuxDesktop();

        if (linux && ContainsPathSegment(trimmed, "/releases/windows"))
            return SwapReleaseChannel(trimmed, "windows", "linux");

        if (!linux && ContainsPathSegment(trimmed, "/releases/linux"))
            return SwapReleaseChannel(trimmed, "linux", "windows");

        return trimmed;
    }

    private static bool NeedsFeedHeal(string? original, string? normalized) =>
        !string.IsNullOrWhiteSpace(original)
        && !string.Equals(
            original.Trim().TrimEnd('/'),
            normalized?.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static string SwapReleaseChannel(string feedUrl, string from, string to)
    {
        // Prefer exact suffix swap; fall back to substring for query-less paths.
        var marker = "/releases/" + from;
        var idx = feedUrl.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return PublicProductionFeedUrl();

        var hostAndPrefix = feedUrl[..idx];
        return hostAndPrefix + "/releases/" + to;
    }

    private static bool ContainsPathSegment(string url, string segment) =>
        url.Contains(segment, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "https://wizionic.com";
        return url.Trim().TrimEnd('/');
    }

    private static bool IsLocalHost(string url) =>
        url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        || url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinuxDesktop() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
}
