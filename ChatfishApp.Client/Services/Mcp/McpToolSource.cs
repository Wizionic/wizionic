using System.Net.Http.Json;
using Microsoft.Extensions.AI;
using ChatfishApp.Client.Services; // WasmKeyStore

namespace ChatfishApp.Client.Services.Mcp;

/// <summary>
/// Builds and caches AITool instances for the currently user-selected remote MCP servers (from the Tools page).
/// 
/// - Reads enabled server names + their tokens from WasmKeyStore.
/// - For each, creates a McpRemoteClient pointed at the RemoteUrl (the one from the registry proxy).
/// - Discovers the server's tools via tools/list.
/// - Wraps each discovered tool in an McpAIFunction (so ME.AI + UseFunctionInvocation can call it).
/// 
/// The list is cached so GetCurrentMcpTools() is fast and synchronous (important because IToolProvider.GetTools is sync).
/// After the user changes checkboxes or tokens on the Tools page we call RefreshFromKeyStoreAsync().
/// </summary>
public class McpToolSource
{
    private readonly WasmKeyStore _keyStore;
    private readonly HttpClient _sharedHttp; // long timeout client for MCP calls

    private List<AITool> _currentMcpTools = new();
    private HashSet<string> _lastEnabledSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public McpToolSource(WasmKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));

        _sharedHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
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
    public async Task RefreshFromKeyStoreAsync()
    {
        var enabledNames = _keyStore.EnabledMcpServerNames;

        // Fast path: nothing changed and we already have tools (or explicitly none)
        lock (_lock)
        {
            if (enabledNames.SetEquals(_lastEnabledSnapshot) && _currentMcpTools.Count > 0)
                return;
        }

        var newTools = new List<AITool>();

        // We need the actual Remote URLs + metadata. The best source is the registry proxy
        // (it already did the hard work of filtering to only http-remotes and picking latest).
        List<RemoteMcpServer> registry = new();
        try
        {
            // Relative "/api/..." calls work from HttpClient in Blazor WASM (resolved against the app origin,
            // the same way pages and the injected client in Tools.razor reach our backend proxy).
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            registry = await http.GetFromJsonAsync<List<RemoteMcpServer>>("/api/tools/mcp-registry") ?? new();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[McpToolSource] Could not fetch registry for discovery: {ex.Message}");
            // Empty list → 0 MCP tools this time. User can hit "Refresh from Registry" on the Tools page
            // (which uses a properly base-addressed HttpClient) to populate the cache.
        }

        var enabledSet = new HashSet<string>(enabledNames, StringComparer.OrdinalIgnoreCase);

        // 1. Registry-provided remote servers (the ones that came back from /api/tools/mcp-registry)
        foreach (var server in registry.Where(s => enabledSet.Contains(s.Name)))
        {
            var token = _keyStore.GetMcpToken(server.Name);
            if (server.RequiresAuth && string.IsNullOrWhiteSpace(token))
            {
                // Record that we skipped it (visible in trace on first use)
                ChatfishApp.Services.Tools.ToolExecutionTrace.Record(
                    $"⚠️ Skipping MCP {server.Name} — token required but not configured");
                continue;
            }

            try
            {
                var client = new McpRemoteClient(server.RemoteUrl, token, _sharedHttp);

                // Discovery (list tools). This is the network cost paid when user changes selection or on first chat.
                var discovered = await client.ListToolsAsync();

                var shortLabel = ShortLabel(server.Name);

                foreach (var def in discovered)
                {
                    var aiTool = new McpAIFunction(
                        client,
                        def.Name,
                        def.Description,
                        def.InputSchema,
                        shortLabel);

                    newTools.Add(aiTool);
                }

                ChatfishApp.Services.Tools.ToolExecutionTrace.Record(
                    $"🔗 Connected to MCP {shortLabel} — {discovered.Count} tool(s) available");
            }
            catch (Exception ex)
            {
                ChatfishApp.Services.Tools.ToolExecutionTrace.Record(
                    $"❌ Failed to connect to MCP {server.Name}: {ex.Message}");
            }
        }

        // 2. User-added custom connectors (from the "Custom Connector" dialog).
        // These have their own persisted URL and are not looked up in the registry.
        foreach (var custom in _keyStore.GetCustomConnectors())
        {
            if (!enabledSet.Contains(custom.Name)) continue;

            var token = _keyStore.GetMcpToken(custom.Name);

            try
            {
                var client = new McpRemoteClient(custom.ServerUrl, token, _sharedHttp);

                var discovered = await client.ListToolsAsync();
                var shortLabel = custom.Name;

                foreach (var def in discovered)
                {
                    var aiTool = new McpAIFunction(
                        client,
                        def.Name,
                        def.Description,
                        def.InputSchema,
                        shortLabel);

                    newTools.Add(aiTool);
                }

                ChatfishApp.Services.Tools.ToolExecutionTrace.Record(
                    $"🔗 Connected to custom MCP {shortLabel} — {discovered.Count} tool(s) available");
            }
            catch (Exception ex)
            {
                ChatfishApp.Services.Tools.ToolExecutionTrace.Record(
                    $"❌ Failed to connect to custom MCP {custom.Name}: {ex.Message}");
            }
        }

        lock (_lock)
        {
            _currentMcpTools = newTools;
            _lastEnabledSnapshot = new HashSet<string>(enabledNames, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ShortLabel(string fullName)
    {
        // Turn "ac.tandem/docs-mcp" or "ai.adweave/meta-ads-mcp" into a short human label
        if (string.IsNullOrWhiteSpace(fullName)) return "mcp";
        var last = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? fullName;
        return last.Length > 28 ? last.Substring(0, 25) + "…" : last;
    }

    // Internal shape matching the registry proxy output (we only need a few fields here)
    private record RemoteMcpServer(string Name, string Description, string RemoteUrl, string Transport, bool RequiresAuth, string? InfoUrl, string Version);
}