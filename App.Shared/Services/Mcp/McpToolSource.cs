using System.Net.Http.Json;
using Microsoft.Extensions.AI;
using App.Core.Auth;
using App.Core.Storage;
using App.Core.Tools;

namespace App.Shared.Services.Mcp;

/// <summary>
/// Builds and caches AITool instances for the currently user-selected remote MCP servers (from the Tools page).
///
/// - Reads enabled server names + their tokens from the KeyStore.
/// - Prefers persisted install URLs (custom / registry install snapshots).
/// - Falls back to registry search-by-name for enabled servers without a stored URL.
/// - Discovers tools via tools/list and wraps them in McpAIFunction.
///
/// The list is cached so GetCurrentMcpTools() is fast and synchronous (IToolProvider.GetTools is sync).
/// After the user installs/disconnects or pastes a token, call RefreshFromKeyStoreAsync().
/// </summary>
public class McpToolSource : IMcpToolRefresher
{
    private readonly IKeyStore _keyStore;
    private readonly IToolExecutionTrace _trace;
    private readonly HttpClient _registryHttp;
    private readonly HttpClient _sharedHttp; // long timeout client for MCP calls

    private List<AITool> _currentMcpTools = new();
    private HashSet<string> _lastEnabledSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public McpToolSource(IKeyStore keyStore, IToolExecutionTrace trace, HttpClient registryHttp, IAuthService? auth = null)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _registryHttp = registryHttp ?? throw new ArgumentNullException(nameof(registryHttp));

        _sharedHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        // Drop cached MCP tools when the signed-in account changes so user B never reuses user A's connectors.
        if (auth is not null)
            auth.OnChanged += () => InvalidateCache();
    }

    private void InvalidateCache()
    {
        lock (_lock)
        {
            _currentMcpTools = new();
            _lastEnabledSnapshot = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Returns the currently discovered MCP tools (may be empty until the first Refresh).
    /// This is called on every tool-using chat turn, so it must be cheap.
    /// </summary>
    public IReadOnlyList<AITool> GetCurrentMcpTools()
    {
        var enabled = _keyStore.EnabledMcpServerNames;

        lock (_lock)
        {
            bool needsLoad = _currentMcpTools.Count == 0 && enabled.Any();
            if (needsLoad)
            {
                // Fire and forget a refresh so the next chat turn (or after a short delay) can pick them up.
                // The first message may only see native tools; subsequent ones will see MCP tools.
                _ = RefreshFromKeyStoreAsync();
            }
            return _currentMcpTools.ToList(); // defensive copy
        }
    }

    /// <summary>
    /// Rebuilds the list of MCP AITools based on whatever is currently enabled + tokenized in the KeyStore.
    /// Safe to call from the Tools page after the user toggles or pastes a token.
    /// </summary>
    public async Task RefreshFromKeyStoreAsync(CancellationToken ct = default)
    {
        var enabledNames = _keyStore.EnabledMcpServerNames;

        // Fast path: nothing changed and we already have tools (or explicitly none)
        lock (_lock)
        {
            if (enabledNames.SetEquals(_lastEnabledSnapshot) && _currentMcpTools.Count > 0)
                return;
        }

        var newTools = new List<AITool>();
        var enabledSet = new HashSet<string>(enabledNames, StringComparer.OrdinalIgnoreCase);

        // Prefer persisted install URLs (custom dialog + registry installs that snapshotted the URL).
        // These survive top-20 browse limits and work without re-fetching the whole registry catalog.
        var customByName = _keyStore.GetCustomConnectors()
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.ServerUrl))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ServerUrl, StringComparer.OrdinalIgnoreCase);

        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Installed connectors with a stored URL.
        foreach (var name in enabledSet)
        {
            if (!customByName.TryGetValue(name, out var url) || string.IsNullOrWhiteSpace(url))
                continue;

            await ConnectAndAddToolsAsync(newTools, name, url, requiresAuth: false, ShortLabel(name), ct);
            connected.Add(name);
        }

        // 2. Fallback: resolve remaining enabled names via registry search (by name).
        foreach (var name in enabledSet.Where(n => !connected.Contains(n)))
        {
            RemoteMcpServer? server = null;
            try
            {
                var list = await _registryHttp.GetFromJsonAsync<List<RemoteMcpServer>>(
                    $"/api/tools/mcp-registry?q={Uri.EscapeDataString(name)}&limit=10", ct) ?? new();
                server = list.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                      ?? list.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[McpToolSource] Registry lookup for {name}: {ex.Message}");
            }

            if (server is null || string.IsNullOrWhiteSpace(server.RemoteUrl))
            {
                _trace.Record($"⚠️ Skipping MCP {name} — no stored URL and not found in registry");
                continue;
            }

            await ConnectAndAddToolsAsync(
                newTools, server.Name, server.RemoteUrl, server.RequiresAuth, ShortLabel(server.Name), ct);
            connected.Add(server.Name);
        }

        lock (_lock)
        {
            _currentMcpTools = newTools;
            _lastEnabledSnapshot = new HashSet<string>(enabledNames, StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task ConnectAndAddToolsAsync(
        List<AITool> newTools,
        string name,
        string remoteUrl,
        bool requiresAuth,
        string shortLabel,
        CancellationToken ct)
    {
        var token = _keyStore.GetMcpToken(name);
        if (requiresAuth && string.IsNullOrWhiteSpace(token))
        {
            _trace.Record($"⚠️ Skipping MCP {name} — token required but not configured");
            return;
        }

        try
        {
            var client = new McpRemoteClient(remoteUrl, token, _sharedHttp);
            var discovered = await client.ListToolsAsync();

            foreach (var def in discovered)
            {
                newTools.Add(new McpAIFunction(
                    client,
                    _trace,
                    def.Name,
                    def.Description,
                    def.InputSchema,
                    shortLabel));
            }

            _trace.Record($"🔗 Connected to MCP {shortLabel} — {discovered.Count} tool(s) available");
        }
        catch (Exception ex)
        {
            _trace.Record($"❌ Failed to connect to MCP {name}: {ex.Message}");
        }
    }

    private static string ShortLabel(string fullName)
    {
        // Turn "ac.tandem/docs-mcp" or "ai.adweave/meta-ads-mcp" into a short human label
        if (string.IsNullOrWhiteSpace(fullName)) return "mcp";
        var last = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? fullName;
        return last.Length > 28 ? last.Substring(0, 25) + "…" : last;
    }

    // Internal shape matching the registry proxy output (extra JSON fields are ignored).
    private record RemoteMcpServer(
        string Name,
        string Description,
        string RemoteUrl,
        string Transport,
        bool RequiresAuth,
        string? InfoUrl,
        string Version);
}
