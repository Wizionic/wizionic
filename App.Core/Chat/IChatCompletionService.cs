using App.Core.Storage;

namespace App.Core.Chat;

public interface IChatCompletionService
{
    /// <param name="onPartialText">
    /// Optional. Invoked with the accumulated assistant display text as tokens stream in
    /// (Ollama, Lemonade, and other OpenAI-compatible clients). Proxied providers may
    /// only call this once with the full text.
    /// </param>
    Task<ChatCompletionResult> CompleteAsync(
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        string? currentUser = null,
        string? conversationId = null,
        CancellationToken ct = default,
        Func<string, Task>? onPartialText = null);
}

public record ChatCompletionResult(
    string Text,
    string ToolTrace,
    string? Error,
    /// <summary>
    /// Media extracted from Omni (or similar) responses — images/audio that were
    /// embedded as data-URIs in the assistant content.
    /// </summary>
    IReadOnlyList<Attachment>? Attachments = null,
    ChatCompletionStats? Stats = null);

/// <summary>Timing and token metrics for a completion (streaming or not).</summary>
public record ChatCompletionStats(
    /// <summary>Client-side prep before the model HTTP call (ms).</summary>
    double PrepMs,
    /// <summary>Time from model request start to first text token (ms). Null if non-streaming or cancelled early.</summary>
    double? TtftMs,
    /// <summary>Wall time for the model call including streaming (ms).</summary>
    double TotalMs,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    bool Streamed,
    bool Cancelled = false,
    /// <summary>Model context window size (tokens), when known.</summary>
    int? ContextLimit = null,
    /// <summary>Tokens used in this request (prompt + completion when available).</summary>
    int? ContextUsed = null,
    /// <summary>How many older history messages were dropped to fit the window.</summary>
    int MessagesTrimmed = 0)
{
    public string FormatLine()
    {
        var parts = new List<string>();
        if (TtftMs is double ttft)
            parts.Add($"TTFT {(ttft / 1000.0):0.00}s");
        parts.Add($"total {(TotalMs / 1000.0):0.00}s");
        if (PrepMs >= 50)
            parts.Add($"prep {(PrepMs / 1000.0):0.00}s");
        if (PromptTokens is int pin)
            parts.Add($"in {pin}");
        if (CompletionTokens is int pout)
            parts.Add($"out {pout}");
        if (CompletionTokens is int c && TotalMs > 0 && !Cancelled)
        {
            var tps = c / (TotalMs / 1000.0);
            if (tps > 0.1)
                parts.Add($"{tps:0.0} tok/s");
        }
        if (ContextUsed is int used && ContextLimit is int limit and > 0)
        {
            var pct = Math.Min(100, (int)Math.Round(100.0 * used / limit));
            parts.Add($"ctx {FormatTokenCount(used)}/{FormatTokenCount(limit)} ({pct}%)");
        }
        else if (ContextUsed is int usedOnly)
        {
            parts.Add($"ctx {FormatTokenCount(usedOnly)}");
        }
        if (MessagesTrimmed > 0)
            parts.Add($"trimmed {MessagesTrimmed}");
        if (Streamed)
            parts.Add("stream");
        if (Cancelled)
            parts.Add("stopped");
        return string.Join(" · ", parts);
    }

    private static string FormatTokenCount(int n)
    {
        if (n >= 1_000_000)
            return $"{n / 1_000_000.0:0.#}M";
        if (n >= 10_000)
            return $"{n / 1000.0:0.#}k";
        if (n >= 1000)
            return $"{n / 1000.0:0.#}k";
        return n.ToString();
    }
}