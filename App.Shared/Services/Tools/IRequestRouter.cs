namespace App.Shared.Services.Tools;

public enum RouteType
{
    StandardChat,
    ToolAssistedChat,
    DirectToolCall,
    AgentTask
}

/// <summary>
/// Tool-module selection for one chat turn. Produced by rules and/or an AI router.
/// </summary>
public record RequestRoute(
    RouteType Type,
    IReadOnlyList<string> Modules,
    string? TargetModule = null,
    bool IncludeMcp = false,
    string? Reason = null,
    /// <summary>Trace label: Rules, AI, Hybrid→Rules, Hybrid→AI, Skill, etc.</summary>
    string? Source = null,
    /// <summary>When set, chat injects that skill's SKILL.md body as system instructions.</summary>
    string? SkillId = null)
{
    public static readonly IReadOnlyList<string> EmptyModules = Array.Empty<string>();

    public static RequestRoute PureChat(string reason, string source = "Rules") =>
        new(RouteType.StandardChat, EmptyModules, Reason: reason, Source: source);

    public static RequestRoute WithModules(
        IEnumerable<string> modules,
        string reason,
        string? targetModule = null,
        bool includeMcp = false,
        string source = "Rules",
        string? skillId = null)
    {
        var list = modules
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new RequestRoute(
            list.Count == 0 && skillId is null ? RouteType.StandardChat : RouteType.ToolAssistedChat,
            list,
            targetModule,
            includeMcp,
            reason,
            source,
            skillId);
    }

    public bool HasTools => Modules.Count > 0 || !string.IsNullOrWhiteSpace(SkillId);
}

/// <summary>
/// Classifies user intent before chat completion — rules, AI model, or hybrid.
/// </summary>
public interface IRequestRouter
{
    Task<RequestRoute> ClassifyRequestAsync(
        string message,
        IReadOnlyList<IToolModule> activeModules,
        string? conversationId = null,
        CancellationToken ct = default);
}
