using App.Core.Storage;
using App.Core.Tools;

namespace App.Core.Lemonade;

/// <summary>
/// After a Lemonade catalog refresh, fill blank Lemonade profile slots and routing
/// so Chat / mic / Speak / Imagine work without a trip to Settings.
/// </summary>
public static class LemonadeWorkspaceSync
{
    public const string ProfileId = "lemonade";

    public static ModelProfile FillProfile(ModelProfile? existing, IReadOnlyList<LemonadeModelSettings> models)
    {
        var p = existing?.Clone() ?? new ModelProfile();
        if (string.IsNullOrWhiteSpace(p.Id))
            p.Id = ProfileId;
        if (string.IsNullOrWhiteSpace(p.Name))
            p.Name = "Lemonade";

        p.ChatModelId = Keep(p.ChatModelId) ?? Qualify(PickChat(models));
        p.RoutingModelId = Keep(p.RoutingModelId) ?? PickRouting(models);
        p.ImageModelId = Keep(p.ImageModelId) ?? Qualify(Pick(models, m => m.IsImage));
        p.EditModelId = Keep(p.EditModelId) ?? Qualify(Pick(models, m => m.IsEdit));
        p.TtsModelId = Keep(p.TtsModelId) ?? Qualify(Pick(models, m => m.IsTts));
        p.SttModelId = Keep(p.SttModelId) ?? Qualify(Pick(models, m => m.IsTranscription));
        if (string.IsNullOrWhiteSpace(p.Voice) && !string.IsNullOrWhiteSpace(p.TtsModelId))
            p.Voice = "shimmer";
        return p;
    }

    public static string? PickRouting(IReadOnlyList<LemonadeModelSettings> models)
    {
        var hit = models.FirstOrDefault(IsQwen35Routing);
        return hit == null ? null : Qualify(hit.Name);
    }

    public static bool ShouldSelectLemonadeProfile(string? lastSelectedModel)
    {
        if (string.IsNullOrWhiteSpace(lastSelectedModel))
            return true;
        if (ModelProfileId.TryParsePicker(lastSelectedModel, out _))
            return false;
        return lastSelectedModel.StartsWith("lemonade/", StringComparison.OrdinalIgnoreCase);
    }

    public static ToolRoutingMode ModeAfterAutoRouting(ToolRoutingMode current, bool filledRoutingModel) =>
        filledRoutingModel && current == ToolRoutingMode.Rules
            ? ToolRoutingMode.Hybrid
            : current;

    private static string? PickChat(IReadOnlyList<LemonadeModelSettings> models)
    {
        var chats = models.Where(m => m.IsChatEligible).ToList();
        if (chats.Count == 0)
            return null;
        var lfm = chats.FirstOrDefault(m =>
            m.Name.Contains("LFM2.5", StringComparison.OrdinalIgnoreCase)
            && m.Name.Contains("1.2B", StringComparison.OrdinalIgnoreCase));
        if (lfm != null)
            return lfm.Name;
        var notRouter = chats.FirstOrDefault(m => !IsQwen35Routing(m));
        return (notRouter ?? chats[0]).Name;
    }

    private static string? Pick(IReadOnlyList<LemonadeModelSettings> models, Func<LemonadeModelSettings, bool> pred) =>
        models.FirstOrDefault(pred)?.Name;

    private static bool IsQwen35Routing(LemonadeModelSettings m) =>
        m.Name.Contains("Qwen3.5", StringComparison.OrdinalIgnoreCase)
        && (m.Name.Contains("0.8B", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("0.8b", StringComparison.OrdinalIgnoreCase));

    private static string? Qualify(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : "lemonade/" + name.Trim();

    private static string? Keep(string? current) =>
        string.IsNullOrWhiteSpace(current) ? null : current.Trim();
}
