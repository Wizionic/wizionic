using Microsoft.Extensions.AI;
using ChatfishApp.Client.Services.Mcp;

namespace ChatfishApp.Services.Tools;

/// <summary>
/// Simple registry of AIFunction tools available to models (WASM/client-side version).
/// Tools run in the browser when using WASM chat.
/// 
/// Now also includes any remote MCP tools the user has enabled on the Tools page
/// (via McpToolSource which reads the enabled set + tokens from WasmKeyStore).
/// </summary>
public class DefaultToolProvider : IToolProvider
{
    private readonly List<AITool> _nativeTools;
    private readonly McpToolSource _mcpSource;

    public DefaultToolProvider(McpToolSource mcpSource)
    {
        _mcpSource = mcpSource ?? throw new ArgumentNullException(nameof(mcpSource));

        // Native built-in tools (web search, summarize, weather, time, calculate) — always available.
        _nativeTools = new List<AITool>
        {
            AIFunctionFactory.Create(AppTools.SearchWeb),
            AIFunctionFactory.Create(AppTools.SummarizeUrl),
            AIFunctionFactory.Create(AppTools.GetCurrentTimeUtc),
            AIFunctionFactory.Create(AppTools.Calculate),
            AIFunctionFactory.Create(AppTools.GetCurrentWeather)
        };
    }

    public IReadOnlyList<AITool> GetTools()
    {
        var all = new List<AITool>(_nativeTools.Count + 8);
        all.AddRange(_nativeTools);

        // Append any currently discovered remote MCP tools (cached in the source).
        // These were built from the user's checkbox selections + stored tokens.
        all.AddRange(_mcpSource.GetCurrentMcpTools());

        return all;
    }
}
