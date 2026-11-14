namespace App.Core.Skills;

/// <summary>How a skill run was started (shown on Skills page history).</summary>
public static class SkillRunSource
{
    public const string Manual = "manual";
    public const string Chat = "chat";
    public const string Workflow = "workflow";
    public const string A2A = "a2a";
}

/// <summary>Run lifecycle. Empty on logs saved before structured steps existed.</summary>
public static class SkillRunState
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

/// <summary>One completed (or failed) skill execution for history UI.</summary>
public sealed class SkillRunLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SkillId { get; set; } = "";
    public string SkillName { get; set; } = "";
    /// <summary>Catalog model id that ran the skill (e.g. lemonade/…, ollama/…).</summary>
    public string ModelId { get; set; } = "";
    /// <summary><see cref="SkillRunSource"/> value: manual | chat | workflow | a2a.</summary>
    public string Source { get; set; } = SkillRunSource.Manual;
    public string? WorkflowId { get; set; }
    public string? WorkflowName { get; set; }
    public string? TriggerDetail { get; set; }
    /// <summary>A2A task id when another device started this run. Empty for local runs.</summary>
    public string? TaskId { get; set; }
    /// <summary><see cref="SkillRunState"/>. Empty on older logs; use <see cref="Success"/> for those.</summary>
    public string State { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ResultText { get; set; }
    /// <summary>Tool/route log lines collected during the run.</summary>
    public List<string> LogLines { get; set; } = new();
    /// <summary>Structured steps. Empty on logs saved before this field existed.</summary>
    public List<SkillStep> Steps { get; set; } = new();
    public double DurationSeconds => Math.Max(0, (EndedAtUtc - StartedAtUtc).TotalSeconds);
}

public interface ISkillRunLogStore
{
    Task LoadAsync(CancellationToken ct = default);
    IReadOnlyList<SkillRunLog> ListRecent(int max = 40);
    SkillRunLog? Get(string id);
    Task AddAsync(SkillRunLog log, CancellationToken ct = default);
    /// <summary>
    /// Insert or replace a log. Feedback already stored on a step is kept when the incoming
    /// step has none, so a later runner save does not drop a note the user just wrote.
    /// </summary>
    Task UpdateAsync(SkillRunLog log, CancellationToken ct = default);
    Task<bool> SetFeedbackAsync(string logId, string stepId, string verdict, string? note, string? by, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    event Action? Changed;
}
