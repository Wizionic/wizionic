using System.Net.Http.Json;
using System.Text;

namespace App.Shared.Services.Connectors;

/// <summary>
/// Executes allowlisted provider HTTP calls via the host proxy (avoids browser CORS).
/// </summary>
public sealed class ConnectorHttpExecutor
{
    private readonly HttpClient _http;

    public ConnectorHttpExecutor(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<(int Status, string Body)> SendAsync(
        string method,
        string url,
        string? bearerToken,
        string? jsonBody = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken ct = default)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            headers["Authorization"] = "Bearer " + bearerToken.Trim();
        if (extraHeaders is not null)
        {
            foreach (var kv in extraHeaders)
                headers[kv.Key] = kv.Value;
        }

        // Provider-specific defaults
        if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            headers.TryAdd("User-Agent", "Wizionic-Connectors");
            headers.TryAdd("Accept", "application/vnd.github+json");
        }
        if (url.Contains("api.notion.com", StringComparison.OrdinalIgnoreCase))
        {
            headers.TryAdd("Notion-Version", "2022-06-28");
            headers.TryAdd("Accept", "application/json");
        }
        if (jsonBody is not null)
            headers.TryAdd("Content-Type", "application/json");

        var resp = await _http.PostAsJsonAsync("/api/connectors/http", new
        {
            method,
            url,
            headers,
            body = jsonBody
        }, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return ((int)resp.StatusCode, err);
        }

        var proxy = await resp.Content.ReadFromJsonAsync<ProxyResponse>(cancellationToken: ct);
        if (proxy is null)
            return (502, "Empty proxy response");
        return (proxy.Status, proxy.Body ?? "");
    }

    private sealed class ProxyResponse
    {
        public int Status { get; set; }
        public string? Body { get; set; }
    }
}
