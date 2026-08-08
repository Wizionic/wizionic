namespace App.Core.Tools;

/// <summary>
/// Ambient conversation id for tool modules during a completion
/// (same pattern as tool execution trace — no AsyncLocal for WASM).
/// </summary>
public interface IToolConversationContext
{
    string? ConversationId { get; set; }
}
