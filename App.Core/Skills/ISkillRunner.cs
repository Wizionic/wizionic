namespace App.Core.Skills;

public interface ISkillRunner
{
    Task<SkillRunResult> RunAsync(SkillRunRequest request, CancellationToken ct = default);
}

public sealed class SkillRunRequest
{
    public string SkillId { get; set; } = "";
    public string? ModelId { get; set; }
    public string? ConversationId { get; set; }
    public string? UserMessageOverride { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
    public string? BodyOverride { get; set; }

    /// <summary>Invoked on the UI thread path with the full live log so far (tool traces).</summary>
    public Action<IReadOnlyList<string>>? OnLog { get; set; }

    /// <summary>Streaming assistant text during the skill run.</summary>
    public Func<string, Task>? OnPartialText { get; set; }
}

public sealed class SkillRunResult
{
    public bool Success { get; set; }
    public string? Text { get; set; }
    public string? ToolTrace { get; set; }
    public string? Error { get; set; }
    public string? ConversationId { get; set; }
    public string? ModelId { get; set; }
    public SkillRunLog? Log { get; set; }
}
