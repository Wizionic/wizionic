using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;

namespace App.Apis;

/// <summary>
/// Allowlisted HTTP proxy so WASM clients can call provider APIs without CORS,
/// without the server storing OAuth tokens.
/// </summary>
public static class ConnectorProxyEndpoints
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.googleapis.com",
        "www.googleapis.com",
        "oauth2.googleapis.com",
        "api.github.com",
        "api.notion.com",
        "api.stripe.com"
    };

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE"
    };

    private const int MaxBodyBytes = 1_000_000;

    public static IEndpointRouteBuilder MapConnectorProxyApis(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/connectors");

        group.MapPost("/http", async (ConnectorHttpRequest req, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Url) || string.IsNullOrWhiteSpace(req.Method))
                return Results.BadRequest(new { message = "Method and Url are required." });

            if (!AllowedMethods.Contains(req.Method.Trim()))
                return Results.BadRequest(new { message = $"Method '{req.Method}' is not allowed." });

            if (!Uri.TryCreate(req.Url.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                return Results.BadRequest(new { message = "Url must be an absolute http(s) URL." });

            if (!AllowedHosts.Contains(uri.Host))
                return Results.BadRequest(new { message = $"Host '{uri.Host}' is not allowlisted." });

            if (req.Body is not null && Encoding.UTF8.GetByteCount(req.Body) > MaxBodyBytes)
                return Results.BadRequest(new { message = "Body too large." });

            using var http = httpFactory.CreateClient("connector-proxy");
            using var message = new HttpRequestMessage(new HttpMethod(req.Method.Trim().ToUpperInvariant()), uri);

            if (req.Headers is not null)
            {
                foreach (var (key, value) in req.Headers)
                {
                    if (string.IsNullOrWhiteSpace(key) || value is null) continue;
                    // Content headers vs request headers
                    if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!message.Headers.TryAddWithoutValidation(key, value))
                    {
                        // ignore unaddable
                    }
                }
            }

            if (req.Body is not null &&
                !string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(req.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                var contentType = "application/json";
                if (req.Headers is not null &&
                    req.Headers.TryGetValue("Content-Type", out var ctHeader) &&
                    !string.IsNullOrWhiteSpace(ctHeader))
                    contentType = ctHeader;
                message.Content = new StringContent(req.Body, Encoding.UTF8, contentType.Split(';')[0].Trim());
            }

            using var resp = await http.SendAsync(message, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            var respHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers)
                respHeaders[h.Key] = string.Join(", ", h.Value);
            if (resp.Content.Headers.ContentType is not null)
                respHeaders["Content-Type"] = resp.Content.Headers.ContentType.ToString();

            return Results.Json(new ConnectorHttpResponse(
                (int)resp.StatusCode,
                respBody,
                respHeaders));
        });

        return endpoints;
    }

    public sealed record ConnectorHttpRequest(
        [property: JsonPropertyName("method")] string? Method,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers,
        [property: JsonPropertyName("body")] string? Body);

    public sealed record ConnectorHttpResponse(
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("headers")] Dictionary<string, string> Headers);
}
