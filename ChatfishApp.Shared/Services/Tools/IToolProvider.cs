using Microsoft.Extensions.AI;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Registry of AIFunction tools available to models.
/// </summary>
public interface IToolProvider
{
    IReadOnlyList<AITool> GetTools();

    /// <param name="includeMcp">When false, skips user-enabled MCP tools (faster pure utility turns).</param>
    IReadOnlyList<AITool> GetToolsForModules(IEnumerable<string> moduleNames, bool includeMcp = true);

    IReadOnlyList<IToolModule> GetActiveModules();
}
