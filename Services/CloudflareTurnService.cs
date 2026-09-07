using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace App.Services;

/// <summary>
/// Fetches short-lived ICE servers from Cloudflare TURN. Token stays on the host.
/// </summary>
public sealed class CloudflareTurnService
{
    public static readonly IceServersResponse Fallback = new()
    {
        IceServers =
        {
            new IceServerDto { Urls = { "stun:stun.l.google.com:19302" } }
        }
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _http;
    private readonly IOptions<TurnOptions> _options;
    private readonly ILogger<CloudflareTurnService> _log;
    private readonly object _gate = new();
    private IceServersResponse? _cache;
    private DateTimeOffset _cacheUntil;

    public CloudflareTurnService(
        IHttpClientFactory http,
        IOptions<TurnOptions> options,
        ILogger<CloudflareTurnService> log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public async Task<IceServersResponse> GetIceServersAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cache is not null && DateTimeOffset.UtcNow < _cacheUntil)
                return _cache;
        }

        var cfg = _options.Value;
        if (!cfg.Cloudflare.IsConfigured)
            return Fallback;

        try
        {
            var ttl = cfg.TtlSeconds <= 0 ? 86400 : cfg.TtlSeconds;
            var client = _http.CreateClient("cloudflare-turn");
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://rtc.live.cloudflare.com/v1/turn/keys/{Uri.EscapeDataString(cfg.Cloudflare.TokenId.Trim())}/credentials/generate-ice-servers");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Cloudflare.ApiToken.Trim());
            req.Content = JsonContent.Create(new { ttl });

            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Cloudflare TURN credentials failed: {Status}", (int)resp.StatusCode);
                return Fallback;
            }

            var parsed = JsonSerializer.Deserialize<IceServersResponse>(body, JsonOpts);
            if (parsed?.IceServers is null || parsed.IceServers.Count == 0)
                return Fallback;

            foreach (var server in parsed.IceServers)
                server.Urls = server.Urls.Where(u => !IsBlockedBrowserPort53(u)).ToList();

            parsed.IceServers = parsed.IceServers.Where(s => s.Urls.Count > 0).ToList();
            if (parsed.IceServers.Count == 0)
                return Fallback;

            var cacheFor = TimeSpan.FromSeconds(Math.Max(60, ttl / 2));
            lock (_gate)
            {
                _cache = parsed;
                _cacheUntil = DateTimeOffset.UtcNow.Add(cacheFor);
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cloudflare TURN credentials request failed");
            return Fallback;
        }
    }

    /// <summary>Browsers block UDP/TCP port 53; Cloudflare includes it as an alternate.</summary>
    internal static bool IsBlockedBrowserPort53(string url)
    {
        var host = url.Split('?', 2)[0];
        return host.EndsWith(":53", StringComparison.Ordinal);
    }
}
