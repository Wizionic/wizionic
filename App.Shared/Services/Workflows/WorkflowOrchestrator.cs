using App.Core.Skills;
using App.Core.Storage;
using App.Core.Workflows;
// CalendarIcs used only if we want RRULE on projected events — currently instances are materialised.

namespace App.Shared.Services.Workflows;

/// <summary>
/// Resolves model + runs skills for wizionic.workflow/v1 definitions;
/// projects cron schedules onto the Workflows calendar.
/// </summary>
public sealed class WorkflowOrchestrator : IWorkflowOrchestrator
{
    private readonly IWorkflowStore _workflows;
    private readonly ISkillRunner _skillRunner;
    private readonly IKeyStore _keys;
    private readonly ICalendarStore? _calendar;

    public WorkflowOrchestrator(
        IWorkflowStore workflows,
        ISkillRunner skillRunner,
        IKeyStore keys,
        ICalendarStore? calendar = null)
    {
        _workflows = workflows;
        _skillRunner = skillRunner;
        _keys = keys;
        _calendar = calendar;
    }

    public async Task<WorkflowRunResult> RunAsync(string workflowId, CancellationToken ct = default)
    {
        await _workflows.LoadAsync(ct);
        var rec = _workflows.Get(workflowId);
        if (rec is null)
            return Fail($"Workflow '{workflowId}' not found.");
        if (!rec.Enabled)
            return Fail($"Workflow '{rec.Name}' is disabled.");

        WorkflowDocument doc;
        try { doc = WorkflowMarkdown.Parse(rec.Yaml); }
        catch (Exception ex) { return Fail("Invalid workflow YAML: " + ex.Message); }

        var err = WorkflowMarkdown.Validate(doc);
        if (err is not null) return Fail(err);

        var model = ResolveModel(doc);
        if (string.IsNullOrWhiteSpace(model))
            return Fail("No model available. Set preferred_model on the workflow or select a chat model.");

        var skillResult = await _skillRunner.RunAsync(new SkillRunRequest
        {
            SkillId = doc.ExecuteSkill.Id,
            ModelId = model,
            UserMessageOverride = doc.ExecuteSkill.UserMessage,
            Parameters = doc.ExecuteSkill.Parameters,
            Source = SkillRunSource.Workflow,
            WorkflowId = doc.Id,
            WorkflowName = string.IsNullOrWhiteSpace(doc.Name) ? doc.Id : doc.Name,
            TriggerDetail = $"trigger={doc.Trigger.Type}"
                + (string.IsNullOrWhiteSpace(doc.Trigger.Expression) ? "" : $" {doc.Trigger.Expression}")
        }, ct);

        rec.LastRunAtUtc = DateTimeOffset.UtcNow;
        rec.LastRunStatus = skillResult.Success ? "ok" : ("error: " + (skillResult.Error ?? "failed"));
        await _workflows.UpsertAsync(rec, ct);

        return new WorkflowRunResult
        {
            Success = skillResult.Success,
            Error = skillResult.Error,
            ModelId = skillResult.ModelId ?? model,
            SkillRunLogId = skillResult.Log?.Id
        };
    }

    public async Task ProjectCalendarsAsync(CancellationToken ct = default)
    {
        if (_calendar is null) return;
        await _workflows.LoadAsync(ct);
        await _calendar.EnsureDefaultCalendarAsync(ct);
        var calId = await _calendar.EnsureWorkflowCalendarAsync(ct);

        // Remove old projected events for managed workflows (by WorkflowId), re-add next 14 days.
        var horizonEnd = DateTime.UtcNow.AddDays(14);
        var existing = await _calendar.LoadEventIndexAsync(DateTime.UtcNow.AddDays(-1), horizonEnd, ct);
        foreach (var idx in existing.Where(e =>
                     e.CalendarId.Equals(calId, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(e.WorkflowId)))
        {
            // Only delete future projected slots we own
            if (idx.StartUtc >= DateTime.UtcNow.AddMinutes(-5))
                await _calendar.SoftDeleteEventAsync(idx.Id, ct);
        }

        var nowLocal = DateTime.Now;
        foreach (var rec in _workflows.List().Where(w => w.Enabled))
        {
            WorkflowDocument doc;
            try { doc = WorkflowMarkdown.Parse(rec.Yaml); }
            catch { continue; }
            if (!doc.Enabled || !doc.Calendar.Project) continue;
            var t = (doc.Trigger.Type ?? "manual").ToLowerInvariant();
            if (t is "manual") continue;
            if (string.IsNullOrWhiteSpace(doc.Trigger.Expression) && t != "once")
                continue;

            var title = string.IsNullOrWhiteSpace(doc.Calendar.Title)
                ? (string.IsNullOrWhiteSpace(doc.Name) ? doc.Id : doc.Name)
                : doc.Calendar.Title!;

            IReadOnlyList<DateTime> next;
            if (t == "once" && CronExpression.TryParseOnceLocal(doc.Trigger.Expression, out var onceLocal))
            {
                next = onceLocal >= nowLocal.AddMinutes(-1) ? new[] { onceLocal } : Array.Empty<DateTime>();
            }
            else if (t == "cron")
            {
                next = CronExpression.NextOccurrences(doc.Trigger.Expression!, nowLocal, 8);
            }
            else
            {
                continue;
            }

            foreach (var localStart in next)
            {
                var startUtc = localStart.ToUniversalTime();
                var endUtc = startUtc.AddMinutes(30);
                var evt = new CalendarEvent(
                    Id: $"wf-{doc.Id}-{localStart:yyyyMMddHHmm}",
                    CalendarId: calId,
                    Summary: title,
                    StartUtc: startUtc,
                    EndUtc: endUtc,
                    IsAllDay: false,
                    Description: $"Wizionic workflow `{doc.Id}` → skill `{doc.ExecuteSkill.Id}`",
                    RRule: null, // instances are materialised; schedule lives on workflow YAML
                    CreatedUtc: DateTime.UtcNow,
                    ModifiedUtc: DateTime.UtcNow,
                    WorkflowId: doc.Id);
                await _calendar.UpsertEventAsync(evt, ct);
            }
        }
    }

    public async Task ProcessDueAsync(CancellationToken ct = default)
    {
        await _workflows.LoadAsync(ct);
        var nowLocal = DateTime.Now;
        foreach (var rec in _workflows.List().Where(w => w.Enabled))
        {
            WorkflowDocument doc;
            try { doc = WorkflowMarkdown.Parse(rec.Yaml); }
            catch { continue; }
            if (!doc.Enabled) continue;
            var t = (doc.Trigger.Type ?? "manual").ToLowerInvariant();
            if (t is "manual") continue;

            DateTime? lastLocal = rec.LastRunAtUtc?.ToLocalTime().DateTime;
            bool due = false;
            if (t == "cron" && !string.IsNullOrWhiteSpace(doc.Trigger.Expression))
            {
                due = CronExpression.IsDue(doc.Trigger.Expression, nowLocal, lastLocal);
            }
            else if (t == "once" && CronExpression.TryParseOnceLocal(doc.Trigger.Expression, out var onceLocal))
            {
                // Fire once when local now is at/after the slot and we have never completed a run.
                due = nowLocal >= onceLocal && rec.LastRunAtUtc is null;
            }

            if (!due) continue;

            try
            {
                await RunAsync(rec.Id, ct);
            }
            catch
            {
                // continue other workflows
            }
        }
    }

    private string? ResolveModel(WorkflowDocument doc)
    {
        var preferred = doc.Orchestrator.PreferredModel?.Trim();
        var fallback = doc.Orchestrator.FallbackModel?.Trim();
        var last = _keys.LastSelectedModel?.Trim();
        var strategy = (doc.Orchestrator.Strategy ?? "fallback_chain").ToLowerInvariant();

        if (strategy == "fixed")
            return FirstNonEmpty(preferred, last, fallback);

        // fallback_chain
        return FirstNonEmpty(preferred, fallback, last);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    private static WorkflowRunResult Fail(string error) => new()
    {
        Success = false,
        Error = error
    };
}

public sealed class NullWorkflowOrchestrator : IWorkflowOrchestrator
{
    public static readonly NullWorkflowOrchestrator Instance = new();
    public Task<WorkflowRunResult> RunAsync(string workflowId, CancellationToken ct = default) =>
        Task.FromResult(new WorkflowRunResult { Success = false, Error = "Workflows run on the app client." });
    public Task ProjectCalendarsAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ProcessDueAsync(CancellationToken ct = default) => Task.CompletedTask;
}
