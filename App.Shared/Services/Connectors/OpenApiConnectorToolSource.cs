using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using App.Core.Auth;
using App.Core.Connectors;
using App.Core.Storage;
using App.Core.Tools;
using Microsoft.Extensions.AI;

namespace App.Shared.Services.Connectors;

/// <summary>
/// Builds and caches AITools for enabled OAuth OpenAPI connectors (curated operations).
/// </summary>
public sealed class OpenApiConnectorToolSource : IOpenApiConnectorRefresher
{
    private readonly IKeyStore _keyStore;
    private readonly IToolExecutionTrace _trace;
    private readonly ConnectorHttpExecutor _http;
    private readonly HttpClient _oauthHttp;

    private List<AITool> _tools = new();
    private string _snapshot = "";
    private readonly object _lock = new();

    public OpenApiConnectorToolSource(
        IKeyStore keyStore,
        IToolExecutionTrace trace,
        ConnectorHttpExecutor http,
        HttpClient oauthHttp,
        IAuthService? auth = null)
    {
        _keyStore = keyStore;
        _trace = trace;
        _http = http;
        _oauthHttp = oauthHttp;
        if (auth is not null)
            auth.OnChanged += () => Invalidate();
    }

    private void Invalidate()
    {
        lock (_lock)
        {
            _tools = new();
            _snapshot = "";
        }
    }

    public IReadOnlyList<AITool> GetCurrentTools()
    {
        lock (_lock)
        {
            var snap = BuildSnapshotKey();
            // Rebuild in background when installs/tokens change (e.g. after sync).
            if (snap != _snapshot)
                _ = RefreshFromKeyStoreAsync();
            return _tools.ToList();
        }
    }

    public async Task RefreshFromKeyStoreAsync(CancellationToken ct = default)
    {
        var snap = BuildSnapshotKey();
        lock (_lock)
        {
            if (snap == _snapshot && _tools.Count > 0)
                return;
        }

        var tools = new List<AITool>();
        foreach (var install in _keyStore.GetOAuthConnectors().Where(c => c.Enabled && c.Tokens is not null))
        {
            var spec = CuratedConnectorSpecs.Get(install.ConnectorId);
            if (spec is null) continue;

            foreach (var op in spec.Operations)
            {
                tools.Add(CreateTool(install.ConnectorId, op));
            }

            _trace.Record(
                $"🔗 OpenAPI connector {install.ConnectorId} — {spec.Operations.Count} tool(s)");
        }

        lock (_lock)
        {
            _tools = tools;
            _snapshot = snap;
        }

        await Task.CompletedTask;
    }

    private string BuildSnapshotKey()
    {
        var parts = _keyStore.GetOAuthConnectors()
            .Where(c => c.Enabled)
            .Select(c =>
                $"{c.ConnectorId}:{(c.Tokens?.AccessToken?.Length ?? 0)}:{(c.Tokens?.ExpiresAtUtc?.UtcTicks ?? 0)}")
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        return string.Join("|", parts);
    }

    private AITool CreateTool(string connectorId, CuratedConnectorOperation op)
    {
        // Dynamic-ish AIFunction: single JSON args bag so we don't generate per-op methods.
        async Task<string> Invoke(
            [Description("JSON object of operation parameters. Path/query args as properties; optional body_json string for JSON request bodies.")]
            string arguments_json = "{}")
        {
            return await InvokeOperationAsync(connectorId, op, arguments_json);
        }

        return AIFunctionFactory.Create(
            Invoke,
            new AIFunctionFactoryOptions
            {
                Name = op.Name,
                Description = BuildDescription(connectorId, op)
            });
    }

    private static string BuildDescription(string connectorId, CuratedConnectorOperation op)
    {
        var sb = new StringBuilder();
        sb.Append(op.Description);
        sb.Append(" (connector: ").Append(connectorId).Append(')');
        if (op.Parameters.Count > 0)
        {
            sb.Append(" Parameters: ");
            sb.Append(string.Join(", ", op.Parameters.Select(p =>
                $"{p.Name}({p.In}{(p.Required ? ", required" : "")})")));
        }
        if (op.RequestBodyJson)
            sb.Append(" Pass body_json with the request JSON body.");
        return sb.ToString();
    }

    private async Task<string> InvokeOperationAsync(
        string connectorId,
        CuratedConnectorOperation op,
        string argumentsJson)
    {
        _trace.Record($"🔌 {op.Name}({Truncate(argumentsJson, 120)})");

        var install = _keyStore.GetOAuthConnector(connectorId);
        if (install?.Tokens is null || string.IsNullOrWhiteSpace(install.Tokens.AccessToken))
        {
            var msg = $"Connector '{connectorId}' is not connected.";
            _trace.Record($"   ❌ {msg}");
            return msg;
        }

        var tokens = install.Tokens;
        tokens = await EnsureFreshTokenAsync(connectorId, install, tokens);

        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(argumentsJson)
                ? new()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson)
                  ?? new();
        }
        catch
        {
            return "Invalid arguments_json; expected a JSON object.";
        }

        string url = op.UrlTemplate;
        var query = new List<string>();
        string? body = null;

        foreach (var p in op.Parameters)
        {
            if (!TryGetArg(args, p.Name, out var val) || val is null)
            {
                if (p.Required)
                    return $"Missing required parameter '{p.Name}'.";
                continue;
            }

            if (p.In.Equals("path", StringComparison.OrdinalIgnoreCase))
                url = url.Replace("{" + p.Name + "}", Uri.EscapeDataString(val), StringComparison.Ordinal);
            else if (p.In.Equals("query", StringComparison.OrdinalIgnoreCase))
                query.Add($"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(val)}");
        }

        if (TryGetArg(args, "body_json", out var bodyJson) && bodyJson is not null)
            body = bodyJson;
        else if (op.RequestBodyJson &&
                 TryGetArg(args, "body", out var bodyAlt) && bodyAlt is not null)
            body = bodyAlt;

        // Defaults for common ops
        if (op.Name.Contains("list_messages", StringComparison.OrdinalIgnoreCase) &&
            !query.Any(q => q.StartsWith("maxResults=", StringComparison.Ordinal)))
            query.Add("maxResults=10");

        if (query.Count > 0)
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + string.Join("&", query);

        var (status, respBody) = await _http.SendAsync(
            op.Method,
            url,
            tokens.AccessToken,
            body,
            ct: default);

        if (status == 401)
        {
            tokens = await EnsureFreshTokenAsync(connectorId, install, tokens, force: true);
            if (tokens is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                (status, respBody) = await _http.SendAsync(
                    op.Method,
                    url,
                    tokens.AccessToken,
                    body);
            }
        }

        var preview = Truncate(respBody, 400);
        if (status is >= 200 and < 300)
            _trace.Record($"   ✅ HTTP {status}: {preview}");
        else
            _trace.Record($"   ❌ HTTP {status}: {preview}");

        return $"HTTP {status}\n{Truncate(respBody, 8000)}";
    }

    private async Task<OAuthTokenSet> EnsureFreshTokenAsync(
        string connectorId,
        OAuthConnectorInstall install,
        OAuthTokenSet tokens,
        bool force = false)
    {
        var expiring = tokens.ExpiresAtUtc is not null
                       && tokens.ExpiresAtUtc < DateTimeOffset.UtcNow.AddMinutes(2);
        if (!force && !expiring)
            return tokens;
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            return tokens;

        // Prefer static catalog for offline; provider id is also on connector install conventions.
        var provider = ConnectorCatalog.GetOAuth(connectorId)?.OAuthProviderId
                       ?? (connectorId.StartsWith("google", StringComparison.OrdinalIgnoreCase) ? "google" : connectorId);
        try
        {
            var resp = await _oauthHttp.PostAsJsonAsync(
                $"/api/oauth/{provider}/refresh",
                new { refreshToken = tokens.RefreshToken });
            if (!resp.IsSuccessStatusCode)
                return tokens;

            var refreshed = await resp.Content.ReadFromJsonAsync<RefreshDto>();
            if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.AccessToken))
                return tokens;

            var next = new OAuthTokenSet(
                refreshed.AccessToken,
                refreshed.RefreshToken ?? tokens.RefreshToken,
                refreshed.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddHours(1),
                refreshed.TokenType ?? tokens.TokenType,
                refreshed.Scope ?? tokens.Scope,
                tokens.AccountLabel ?? install.AccountLabel);

            await _keyStore.UpsertOAuthConnectorAsync(install with
            {
                Tokens = next,
                AccountLabel = next.AccountLabel
            });

            return next;
        }
        catch (Exception ex)
        {
            _trace.Record($"   ⚠️ token refresh failed: {ex.Message}");
            return tokens;
        }
    }

    private static bool TryGetArg(Dictionary<string, JsonElement> args, string name, out string? value)
    {
        value = null;
        foreach (var kv in args)
        {
            if (!kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = kv.Value.ValueKind switch
            {
                JsonValueKind.String => kv.Value.GetString(),
                JsonValueKind.Number => kv.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => kv.Value.GetRawText()
            };
            return true;
        }
        return false;
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }

    private sealed class RefreshDto
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTimeOffset? ExpiresAtUtc { get; set; }
        public string? TokenType { get; set; }
        public string? Scope { get; set; }
    }
}
