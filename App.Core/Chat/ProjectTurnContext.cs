using App.Core.Storage;

namespace App.Core.Chat;

/// <summary>Request-only context, appended after the transcript and never persisted.</summary>
public static class ProjectTurnContext
{
    private const string Context = "project-context/v1";
    private const string Draft = "project-pdf-draft/v1";

    public static ChatMessage Create(string text, bool draft) =>
        new(Role: "system", Content: text, ContentFormat: draft ? Draft : Context);

    public static bool IsContext(ChatMessage? message) =>
        message is { Role: "system", ContentFormat: Context or Draft };

    public static bool IsDraft(ChatMessage? message) =>
        message is { Role: "system", ContentFormat: Draft };
}
