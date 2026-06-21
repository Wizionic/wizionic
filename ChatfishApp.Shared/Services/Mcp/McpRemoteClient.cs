using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatfishApp.Shared.Services.Mcp;

/// <summary>
/// Lightweight JSON-RPC 2.0 client for remote MCP servers using the streamable-http (or SSE-compatible) transport.
/// Used to discover tools (tools/list) and invoke them (tools/call) from the WASM client.
/// 
/// Does a best-effort initialize handshake. Many hosted MCP servers are lenient and will accept direct tools/list.
/// Auth (if needed) is passed as Authorization: Bearer &lt;token&gt;.
/// </summary>
public class McpRemoteClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _bearer;
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public McpRemoteClient(string remoteUrl, string? bearerToken = null, HttpClient? httpClient = null)
    {
        _endpoint = remoteUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(remoteUrl));
        _bearer = string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken.Trim();

        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        if (_bearer != null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        }
    }

    public void Dispose()
    {
        // Only dispose if we own it
        if (_http != null && /* heuristic: we created it */ true) { /* leave open for reuse in real impl */ }
    }

    /// <summary>Perform a minimal initialize handshake (protocolVersion + capabilities).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        var req = new JsonRpcRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = "initialize",
            Params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { tools = new { } },
                clientInfo = new { name = "chatfish-wasm", version = "1.0" }
            }
        };

        try
        {
            _ = await SendAsync<object>(req, ct); // we ignore the server capabilities for now
            _initialized = true;
        }
        catch
        {
            // Many public MCP servers don't strictly require initialize before tools/list.
            // Proceed anyway — the next call will surface real errors.
            _initialized = true;
        }
    }

    public async Task<List<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        var req = new JsonRpcRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = "tools/list"
        };

        var result = await SendAsync<JsonElement>(req, ct);

        var tools = new List<McpToolDefinition>();
        if (result.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in toolsEl.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString() ?? "";
                var desc = t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                JsonElement? schema = t.TryGetProperty("inputSchema", out var s) && s.ValueKind != JsonValueKind.Null ? s : null;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    tools.Add(new McpToolDefinition(name, desc, schema));
                }
            }
        }
        return tools;
    }

    /// <summary>
    /// Calls a tool on the remote MCP and returns a human-readable string of the content.
    /// MCP result shape: { content: [{type:"text", text:"..."}, ...], isError: bool }
    /// </summary>
    public async Task<string> CallToolAsync(string toolName, Dictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        var req = new JsonRpcRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = "tools/call",
            Params = new
            {
                name = toolName,
                arguments = arguments ?? new Dictionary<string, object?>()
            }
        };

        var result = await SendAsync<JsonElement>(req, ct);

        if (result.TryGetProperty("isError", out var isErr) && isErr.GetBoolean())
        {
            return $"[MCP tool error] {ExtractTextContent(result)}";
        }

        return ExtractTextContent(result);
    }

    private static string ExtractTextContent(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return result.ToString();

        var parts = new List<string>();
        foreach (var c in content.EnumerateArray())
        {
            if (c.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                c.TryGetProperty("text", out var text))
            {
                parts.Add(text.GetString() ?? "");
            }
            else if (c.TryGetProperty("data", out var data))
            {
                parts.Add($"[binary {c.GetProperty("mimeType").GetString() ?? "data"}]");
            }
        }
        return string.Join("\n", parts);
    }

    private async Task<T> SendAsync<T>(JsonRpcRequest request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // streamable-http often wants this Accept to allow either JSON or SSE
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = content
        };
        reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(reqMsg, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);

        // Very naive SSE handling: if the body starts with "event:" or "data:", try to extract the last data: JSON blob.
        if (body.StartsWith("event:") || body.Contains("\ndata:"))
        {
            var lastData = body.Split('\n')
                .Where(l => l.StartsWith("data:"))
                .Select(l => l.Substring(5).Trim())
                .LastOrDefault();
            if (!string.IsNullOrWhiteSpace(lastData))
                body = lastData;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "unknown MCP error";
            throw new InvalidOperationException($"MCP error: {msg}");
        }

        if (root.TryGetProperty("result", out var res))
        {
            return JsonSerializer.Deserialize<T>(res.GetRawText(), JsonOpts)!;
        }

        // Some servers return the result directly
        return JsonSerializer.Deserialize<T>(body, JsonOpts)!;
    }

    private sealed class JsonRpcRequest
    {
        public string Jsonrpc { get; set; } = "2.0";
        public string Id { get; set; } = "";
        public string Method { get; set; } = "";
        public object? Params { get; set; }
    }
}

/// <summary>
/// Represents one tool discovered from an MCP server.
/// </summary>
public record McpToolDefinition(string Name, string Description, JsonElement? InputSchema);