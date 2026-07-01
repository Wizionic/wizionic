using ChatfishApp.Core.Storage;

namespace ChatfishApp.Core.Chat;

public interface IChatCompletionService
{
    Task<ChatCompletionResult> CompleteAsync(
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        string? currentUser = null,
        string? conversationId = null,
        CancellationToken ct = default);
}

public record ChatCompletionResult(string Text, string ToolTrace, string? Error);