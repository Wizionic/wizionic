using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChatfishApp.Core.SmartHome;
using ChatfishApp.Core.Storage;
using Microsoft.Extensions.Logging;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Direct LAN client for a local Home Assistant instance.
/// </summary>
public sealed class HomeAssistantService : ISmartHomeService
{
    private readonly IKeyStore _keyStore;
    private readonly ILogger<HomeAssistantService> _logger;
    private readonly HttpClient _http;

    public HomeAssistantService(IKeyStore keyStore, ILogger<HomeAssistantService> logger)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = CreateHttpClient();
    }

    public bool IsConfigured =>
        HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out _, out _);

    public async Task<string> TestConnectionAsync(string baseUrl, string token, CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(baseUrl, token, out var url, out var normalizedToken))
            return "Enter both base URL and access token.";

        Log($"TestConnection starting → {DescribeEndpoint(url)}/api/ (token length {normalizedToken.Length})");

        var apiResult = await SendAsync(HttpMethod.Get, $"{url}/api/", normalizedToken, content: null, ct, "TestConnection/api");
        if (apiResult.StartsWith("HA error", StringComparison.OrdinalIgnoreCase) ||
            apiResult.StartsWith("Connection", StringComparison.OrdinalIgnoreCase))
            return apiResult;

        Log("TestConnection /api/ OK — probing sun.sun entity state");
        var stateResult = await SendAsync(
            HttpMethod.Get,
            $"{url}/api/states/sun.sun",
            normalizedToken,
            content: null,
            ct,
            "TestConnection/sun.sun");

        if (stateResult.StartsWith("HA error", StringComparison.OrdinalIgnoreCase) ||
            stateResult.StartsWith("Connection", StringComparison.OrdinalIgnoreCase))
            return stateResult;

        Log("TestConnection completed successfully");
        return stateResult;
    }

    public Task<string> CallServiceAsync(
        string domain,
        string service,
        object serviceData,
        CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return Task.FromResult("Home Assistant is not configured. Add base URL and token in Settings.");

        var endpoint = $"{url}/api/services/{domain}/{service}";
        return SendAsync(HttpMethod.Post, endpoint, token, JsonContent.Create(serviceData), ct, $"CallService/{domain}.{service}");
    }

    public Task<string> GetEntityStateAsync(string entityId, CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return Task.FromResult("Home Assistant is not configured. Add base URL and token in Settings.");

        return GetEntityStateAsync(url, token, entityId, ct);
    }

    public async Task<string> ListLightEntitiesAsync(CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return "Home Assistant is not configured. Add base URL and token in Settings.";

        var json = await SendAsync(HttpMethod.Get, $"{url}/api/states", token, content: null, ct, "ListLights");
        if (json.StartsWith("HA error", StringComparison.OrdinalIgnoreCase) ||
            json.StartsWith("Connection", StringComparison.OrdinalIgnoreCase) ||
            json.StartsWith("Home Assistant", StringComparison.OrdinalIgnoreCase))
            return json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var lines = new List<string>();
            foreach (var entity in doc.RootElement.EnumerateArray())
            {
                if (!entity.TryGetProperty("entity_id", out var idEl))
                    continue;

                var entityId = idEl.GetString() ?? "";
                if (!entityId.StartsWith("light.", StringComparison.OrdinalIgnoreCase))
                    continue;

                var state = entity.TryGetProperty("state", out var stateEl) ? stateEl.GetString() ?? "unknown" : "unknown";
                var friendly = entityId;
                if (entity.TryGetProperty("attributes", out var attrs) &&
                    attrs.TryGetProperty("friendly_name", out var fn))
                    friendly = fn.GetString() ?? entityId;

                lines.Add($"{friendly} → {entityId} (currently {state})");
            }

            lines.Sort(StringComparer.OrdinalIgnoreCase);
            if (lines.Count == 0)
                return "No light.* entities found in Home Assistant.";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {lines.Count} light(s):");
            foreach (var line in lines)
                sb.AppendLine($"  • {line}");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Could not parse Home Assistant states: {ex.Message}";
        }
    }

    private Task<string> GetEntityStateAsync(
        string baseUrl,
        string token,
        string entityId,
        CancellationToken ct)
    {
        if (!HomeAssistantCredentials.TryNormalize(baseUrl, token, out var url, out var normalizedToken))
            return Task.FromResult("Enter both base URL and access token.");

        if (string.IsNullOrWhiteSpace(entityId))
            return Task.FromResult("Entity ID is required.");

        var endpoint = $"{url}/api/states/{Uri.EscapeDataString(entityId)}";
        return SendAsync(HttpMethod.Get, endpoint, normalizedToken, content: null, ct, $"GetEntityState/{entityId}");
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string url,
        string token,
        HttpContent? content,
        CancellationToken ct,
        string operation)
    {
        var sw = Stopwatch.StartNew();
        Log($"HTTP {method} {DescribeEndpoint(url)} [{operation}]");

        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await _http.SendAsync(request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            Log($"HTTP {method} {DescribeEndpoint(url)} → {(int)resp.StatusCode} {resp.StatusCode} in {sw.ElapsedMilliseconds}ms, body {body.Length} chars [{operation}]");

            if (resp.IsSuccessStatusCode)
                return body;

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                return $"HA error Unauthorized: token rejected (length {token.Length} chars). " +
                       "Re-paste the long-lived token from HA Profile → Security, then test again. " +
                       "Note: MAUI talks to Home Assistant directly — these calls won't appear in browser DevTools.";
            }

            return $"HA error {resp.StatusCode}: {body}";
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"HTTP {method} {DescribeEndpoint(url)} FAILED after {sw.ElapsedMilliseconds}ms [{operation}]: {DescribeException(ex)}");
            return FormatNetworkError(ex, url, sw.Elapsed, operation, ct);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // LAN IPs must not go through the system proxy (common cause of 15s+ hangs).
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    private string FormatNetworkError(Exception ex, string url, TimeSpan elapsed, string operation, CancellationToken ct)
    {
        var endpoint = DescribeEndpoint(url);
        var root = ex.InnerException ?? ex;

        if (ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"Connection timed out after {elapsed.TotalSeconds:0.#}s reaching {endpoint} [{operation}]. " +
                   "Checks: Home Assistant is running, the IP/port is correct, this PC is on the same LAN, " +
                   "and Windows Firewall allows Chatfish on Private networks. " +
                   $"Details: {DescribeException(root)}";
        }

        if (root is HttpRequestException or SocketException)
        {
            return $"Connection failed to {endpoint} after {elapsed.TotalSeconds:0.#}s [{operation}]. " +
                   $"Details: {DescribeException(root)}";
        }

        return $"Connection failed to {endpoint} [{operation}]: {DescribeException(ex)}";
    }

    private void Log(string message)
    {
        _logger.LogInformation("{Message}", message);
        Console.WriteLine($"[HomeAssistant] {message}");
        Debug.WriteLine($"[HomeAssistant] {message}");
    }

    private static string DescribeEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
    }

    private static string DescribeException(Exception ex)
    {
        var parts = new List<string> { $"{ex.GetType().Name}: {ex.Message}" };
        if (ex.InnerException != null)
            parts.Add($"inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        return string.Join(" | ", parts);
    }
}