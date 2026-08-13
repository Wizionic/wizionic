namespace App.Core.Workflows;

/// <summary>
/// Wizionic workflow v1 — thin trigger layer above Agent Skills.
/// Schema: wizionic.workflow/v1 (custom; not full CNCF OWS runtime).
/// </summary>
public sealed class WorkflowDocument
{
    public string Schema { get; set; } = WorkflowMarkdown.SchemaId;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public WorkflowTrigger Trigger { get; set; } = new();
    public WorkflowOrchestration Orchestrator { get; set; } = new();
    public WorkflowExecuteSkill ExecuteSkill { get; set; } = new();
    public WorkflowCalendarProjection Calendar { get; set; } = new();
}

public sealed class WorkflowTrigger
{
    /// <summary>cron | once | manual</summary>
    public string Type { get; set; } = "manual";
    /// <summary>5-field cron (min hour dom mon dow), local device time.</summary>
    public string? Expression { get; set; }
    public string Timezone { get; set; } = "local";
}

/// <summary>Model selection block inside workflow YAML (not the runtime service).</summary>
public sealed class WorkflowOrchestration
{
    /// <summary>fixed | fallback_chain</summary>
    public string Strategy { get; set; } = "fallback_chain";
    public string? PreferredModel { get; set; }
    public string? FallbackModel { get; set; }
}

public sealed class WorkflowExecuteSkill
{
    public string Id { get; set; } = "";
    public string? UserMessage { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkflowCalendarProjection
{
    public bool Project { get; set; } = true;
    public string? Title { get; set; }
    public string Color { get; set; } = "#7c3aed";
}

/// <summary>Persisted workflow package (full YAML text).</summary>
public sealed class WorkflowRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Yaml { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Last successful/failed fire (UTC).</summary>
    public DateTimeOffset? LastRunAtUtc { get; set; }
    public string? LastRunStatus { get; set; }
}

public interface IWorkflowStore
{
    Task LoadAsync(CancellationToken ct = default);
    IReadOnlyList<WorkflowRecord> List();
    WorkflowRecord? Get(string idOrName);
    Task UpsertAsync(WorkflowRecord record, CancellationToken ct = default);
    Task DeleteAsync(string idOrName, CancellationToken ct = default);
    Task ReplaceAllAsync(IEnumerable<WorkflowRecord> records, CancellationToken ct = default);
    event Action? Changed;
}

public interface IWorkflowOrchestrator
{
    /// <summary>Run a workflow now (manual or due schedule).</summary>
    Task<WorkflowRunResult> RunAsync(string workflowId, CancellationToken ct = default);

    /// <summary>Project enabled cron workflows onto the Workflows calendar (next N days).</summary>
    Task ProjectCalendarsAsync(CancellationToken ct = default);

    /// <summary>Find due workflows and run them (app resume / timer).</summary>
    Task ProcessDueAsync(CancellationToken ct = default);
}

public sealed class WorkflowRunResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? SkillRunLogId { get; set; }
    public string? ModelId { get; set; }
}
