using ChatfishApp.Shared.Services.Mcp;
using Microsoft.Extensions.AI;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Composes injectable tool modules with cached MCP tools.
/// </summary>
public sealed class CompositeToolProvider : IToolProvider
{
    private readonly IEnumerable<IToolModule> _modules;
    private readonly McpToolSource _mcpSource;

    public CompositeToolProvider(IEnumerable<IToolModule> modules, McpToolSource mcpSource)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _mcpSource = mcpSource ?? throw new ArgumentNullException(nameof(mcpSource));
    }

    public IReadOnlyList<AITool> GetTools() => BuildToolList(null);

    public IReadOnlyList<AITool> GetToolsForModules(IEnumerable<string> moduleNames)
    {
        var names = new HashSet<string>(moduleNames, StringComparer.OrdinalIgnoreCase);
        return BuildToolList(names);
    }

    public IReadOnlyList<IToolModule> GetActiveModules() =>
        _modules.Where(m => m.IsAvailable).ToList();

    private IReadOnlyList<AITool> BuildToolList(HashSet<string>? moduleFilter)
    {
        var tools = new List<AITool>();

        foreach (var module in _modules)
        {
            if (!module.IsAvailable)
                continue;

            if (moduleFilter != null && !moduleFilter.Contains(module.ModuleName))
                continue;

            tools.AddRange(module.GetTools());
        }

        tools.AddRange(_mcpSource.GetCurrentMcpTools());
        return tools;
    }
}