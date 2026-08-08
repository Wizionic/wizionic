using App.Core.Tools;

namespace App.Shared.Services.Tools;

/// <summary>
/// Scoped conversation id set by ChatCompletionService for the duration of a completion.
/// Instance field (not AsyncLocal) for Blazor WASM compatibility.
/// </summary>
public sealed class ToolConversationContext : IToolConversationContext
{
    public string? ConversationId { get; set; }
}
