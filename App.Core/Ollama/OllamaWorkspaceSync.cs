using App.Core.Storage;
using App.Core.Tools;

namespace App.Core.Ollama;

public static class OllamaWorkspaceSync
{
    public static string? PickRouting(IReadOnlyList<OllamaModelSettings> models)
    {
        var hit = models.FirstOrDefault(m => WizionicRouterModel.Matches(m.Name));
        return hit == null ? null : "ollama/" + hit.Name.Trim();
    }
}