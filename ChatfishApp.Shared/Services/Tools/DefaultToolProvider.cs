using Microsoft.Extensions.AI;
using ChatfishApp.Shared.Services.Mcp;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Registry of native AIFunction tools plus any remote MCP tools enabled on the Tools page.
/// </summary>
public sealed class DefaultToolProvider : IToolProvider
{
    private readonly List<AITool> _nativeTools;
    private readonly McpToolSource _mcpSource;

    public DefaultToolProvider(McpToolSource mcpSource)
    {
        _mcpSource = mcpSource ?? throw new ArgumentNullException(nameof(mcpSource));

        _nativeTools =
        [
            AIFunctionFactory.Create(AppTools.SearchWeb),
            AIFunctionFactory.Create(AppTools.SummarizeUrl),
            AIFunctionFactory.Create(AppTools.GetCurrentTimeUtc),
            AIFunctionFactory.Create(AppTools.Calculate),
            AIFunctionFactory.Create(AppTools.GetCurrentWeather)
        ];
    }

    public IReadOnlyList<AITool> GetTools()
    {
        var all = new List<AITool>(_nativeTools.Count + 8);
        all.AddRange(_nativeTools);
        all.AddRange(_mcpSource.GetCurrentMcpTools());
        return all;
    }
}