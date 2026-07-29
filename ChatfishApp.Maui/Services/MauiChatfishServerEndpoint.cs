using System.Text.Json;
using ChatfishApp.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Mutable MAUI login-server URL: persists to appsettings.Local.json and applies live to HttpClient + cookies.
/// </summary>
public sealed class MauiChatfishServerEndpoint : IChatfishServerEndpoint
{
    private readonly HttpClient _http;
    private readonly MauiAuthCookieStore _cookieStore;
    private readonly ILogger<MauiChatfishServerEndpoint> _logger;
    private string _baseUrl;
    private string? _updateFeedUrlOverride;

    public MauiChatfishServerEndpoint(
        IOptions<ChatfishServerOptions> options,
        HttpClient http,
        MauiAuthCookieStore cookieStore,
        ILogger<MauiChatfishServerEndpoint> logger)
    {
        var opts = options.Value;
        _baseUrl = NormalizeBaseUrl(opts.BaseUrl);
        _updateFeedUrlOverride = string.IsNullOrWhiteSpace(opts.UpdateFeedUrl) ? null : opts.UpdateFeedUrl.TrimEnd('/');
        _http = http;
        _cookieStore = cookieStore;
        _logger = logger;
        ApplyLive();
    }

    public string BaseUrl => _baseUrl;

    public string UpdateFeedUrl =>
        !string.IsNullOrWhiteSpace(_updateFeedUrlOverride)
            ? _updateFeedUrlOverride!
            : (IsLocalHost(_baseUrl)
                ? "https://chatfish.me/releases/windows"
                : _baseUrl.TrimEnd('/') + "/" + ChatfishServerOptions.DefaultUpdateFeedPath);

    public string SyncHubUrl => new Uri(BaseUri, "sync-hub").ToString();

    public Uri BaseUri => new(_baseUrl.TrimEnd('/') + "/");

    public event Action? OnChanged;

    public async Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Login server URL is required.", nameof(baseUrl));

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Enter a valid http(s) URL.", nameof(baseUrl));

        _baseUrl = normalized.TrimEnd('/');

        // When pointing at a local homeserver, keep Velopack updates on the public feed.
        if (IsLocalHost(_baseUrl) && string.IsNullOrWhiteSpace(_updateFeedUrlOverride))
            _updateFeedUrlOverride = "https://chatfish.me/releases/windows";

        await PersistAsync(cancellationToken);
        ApplyLive();
        OnChanged?.Invoke();
        _logger.LogInformation("[ServerEndpoint] Login server set to {BaseUrl}", _baseUrl);
    }

    private void ApplyLive()
    {
        _http.BaseAddress = BaseUri;
        _cookieStore.Configure(new ChatfishServerOptions
        {
            BaseUrl = _baseUrl,
            UpdateFeedUrl = _updateFeedUrlOverride
        });
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var path = Path.Combine(MauiAppData.Directory, "appsettings.Local.json");
        Directory.CreateDirectory(MauiAppData.Directory);
        var payload = new
        {
            ChatfishServer = new
            {
                BaseUrl = _baseUrl,
                UpdateFeedUrl = _updateFeedUrlOverride
            }
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
    }

    private static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "https://chatfish.me";
        return url.Trim().TrimEnd('/');
    }

    private static bool IsLocalHost(string url) =>
        url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        || url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);
}
