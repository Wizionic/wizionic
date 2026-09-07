using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Sync;

namespace App.Shared.Services;

/// <summary>Authenticated GET /api/sync/ice-servers with a short in-memory cache.</summary>
public sealed class HttpIceServerSource : IIceServerSource
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly IceServerDto FallbackStun = new()
    {
        Urls = { "stun:stun.l.google.com:19302" }
    };

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private IReadOnlyList<IceServerDto>? _cache;
    private DateTimeOffset _cacheUntil;

    public HttpIceServerSource(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<IceServerDto>> GetIceServersAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cache is not null && DateTimeOffset.UtcNow < _cacheUntil)
                return _cache;
        }

        try
        {
            var resp = await _http.GetFromJsonAsync<IceServersResponse>("api/sync/ice-servers", JsonOpts, ct);
            var list = resp?.IceServers?
                .Where(s => s.Urls.Count > 0)
                .ToList();
            if (list is null || list.Count == 0)
                return new[] { FallbackStun };

            lock (_gate)
            {
                _cache = list;
                _cacheUntil = DateTimeOffset.UtcNow.AddHours(1);
            }

            return list;
        }
        catch
        {
            return new[] { FallbackStun };
        }
    }
}
