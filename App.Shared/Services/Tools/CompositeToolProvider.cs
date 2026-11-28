using App.Shared.Services.Connectors;
using App.Shared.Services.Mcp;
using Microsoft.Extensions.AI;

namespace App.Shared.Services.Tools;

/// <summary>
/// Composes injectable tool modules with cached MCP + OAuth OpenAPI tools.
/// </summary>
public sealed class CompositeToolProvider : IToolProvider
{
    private readonly IEnumerable<IToolModule> _modules;
    private readonly McpToolSource _mcpSource;
    private readonly OpenApiConnectorToolSource? _openApiSource;
    private readonly BuiltinToolGate _gate;

    public CompositeToolProvider(
        IEnumerable<IToolModule> modules,
        McpToolSource mcpSource,
        BuiltinToolGate gate,
        OpenApiConnectorToolSource? openApiSource = null)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _mcpSource = mcpSource ?? throw new ArgumentNullException(nameof(mcpSource));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _openApiSource = openApiSource;
    }

    public IReadOnlyList<AITool> GetTools() => BuildToolList(null, includeMcp: true);

    public IReadOnlyList<AITool> GetToolsForModules(IEnumerable<string> moduleNames, bool includeMcp = true)
    {
        var names = new HashSet<string>(moduleNames, StringComparer.OrdinalIgnoreCase);
        return BuildToolList(names, includeMcp);
    }

    public IReadOnlyList<IToolModule> GetActiveModules() =>
        _modules.Where(m => m.IsAvailable && _gate.IsModuleEnabled(m.ModuleName)).ToList();

    private IReadOnlyList<AITool> BuildToolList(HashSet<string>? moduleFilter, bool includeMcp)
    {
        var tools = new List<AITool>();

        foreach (var module in _modules)
        {
            if (!module.IsAvailable)
                continue;

            if (!_gate.IsModuleEnabled(module.ModuleName))
                continue;

            if (moduleFilter != null && !moduleFilter.Contains(module.ModuleName))
                continue;

            tools.AddRange(_gate.FilterTools(module.ModuleName, module.GetTools()));
        }

        if (includeMcp)
        {
            tools.AddRange(_mcpSource.GetCurrentMcpTools());
            if (_openApiSource is not null)
                tools.AddRange(_openApiSource.GetCurrentTools());
        }
        return tools;
    }
}