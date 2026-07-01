using Microsoft.Extensions.AI;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Registry of AIFunction tools available to models.
/// </summary>
public interface IToolProvider
{
    IReadOnlyList<AITool> GetTools();

    IReadOnlyList<AITool> GetToolsForModules(IEnumerable<string> moduleNames);

    IReadOnlyList<IToolModule> GetActiveModules();
}