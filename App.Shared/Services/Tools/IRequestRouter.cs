namespace App.Shared.Services.Tools;

public enum RouteType
{
    StandardChat,
    ToolAssistedChat,
    DirectToolCall,
    AgentTask
}

public record RequestRoute(RouteType Type, string? TargetModule = null);

/// <summary>
/// Classifies user intent before chat completion. Hook for future routing models.
/// </summary>
public interface IRequestRouter
{
    RequestRoute ClassifyRequest(
        string message,
        IReadOnlyList<IToolModule> activeModules,
        string? conversationId = null);
}