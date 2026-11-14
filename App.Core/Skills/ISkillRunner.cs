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

    /// <summary><see cref="SkillRunSource"/> — manual | chat | workflow | a2a.</summary>
    public string Source { get; set; } = SkillRunSource.Manual;
    public string? WorkflowId { get; set; }
    public string? WorkflowName { get; set; }
    public string? TriggerDetail { get; set; }
    /// <summary>A2A task id when a remote orchestrator started this run.</summary>
    public string? TaskId { get; set; }

    /// <summary>Invoked on the UI thread path with the full live log so far (tool traces).</summary>
    public Action<IReadOnlyList<string>>? OnLog { get; set; }

    /// <summary>Structured steps so far, including the line just recorded.</summary>
    public Action<IReadOnlyList<SkillStep>>? OnSteps { get; set; }

    /// <summary>Called once the run log id is stored, so feedback can be saved mid-run.</summary>
    public Action<string>? OnRunStarted { get; set; }

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
