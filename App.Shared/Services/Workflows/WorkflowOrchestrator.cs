using App.Core.Skills;
using App.Core.Storage;
using App.Core.Workflows;
using App.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceProvider _services;

    public WorkflowOrchestrator(
        IWorkflowStore workflows,
        ISkillRunner skillRunner,
        IKeyStore keys,
        IServiceProvider services,
        ICalendarStore? calendar = null)
    {
        _workflows = workflows;
        _skillRunner = skillRunner;
        _keys = keys;
        _calendar = calendar;
        _services = services;
    }

    public async Task<WorkflowRunResult> RunAsync(string workflowId, CancellationToken ct = default)
    {
        await _workflows.LoadAsync(ct);
        var rec = _workflows.Get(workflowId);
        if (rec is null)
            return Fail($"Workflow '{workflowId}' not found.");
        if (!rec.Enabled)
            return Fail($"Workflow '{rec.Name}' is disabled.");

        using var scope=OperatingSystem.IsBrowser() ? null : _services.CreateScope();
        var services=scope?.ServiceProvider??_services;
        var projects=services.GetRequiredService<ProjectStore>();
        var sync=services.GetRequiredService<ISyncService>();
        ProjectWorkflow? link=null;string? project=null;
        if(sync.MyDeviceId!=null)foreach(var row in (await projects.IndexAsync()).Where(r=>r.Id.Contains(":w:")&&!ProjectStore.Deleted(r)))
        {
            link=await projects.ReadAsync<ProjectWorkflow>(row.Id);
            if(link?.Id!=rec.Id || link.DeviceId!=sync.MyDeviceId)continue;
            project=row.Id.Split(":w:")[0];
            break;
        }
        var result=await ExecuteAsync(rec,project,services,ct);
        rec.LastRunAtUtc = DateTimeOffset.UtcNow;
        rec.LastRunStatus = result.Success ? "ok" : ("error: " + (result.Error ?? "failed"));
        await _workflows.UpsertAsync(rec, ct);
        return result;
    }

    public async Task<WorkflowRunResult> RunProjectAsync(string linkId, CancellationToken ct=default)
    {
        using var scope=OperatingSystem.IsBrowser() ? null : _services.CreateScope();
        var services=scope?.ServiceProvider??_services;
        var link=await services.GetRequiredService<ProjectStore>().ReadAsync<ProjectWorkflow>(linkId);
        if(link==null)return Fail("Workflow link was removed.");
        return await ExecuteAsync(new WorkflowRecord{Id=link.Id,Name=link.Name,Yaml=link.Yaml},linkId.Split(":w:")[0],services,ct);
    }

    private async Task<WorkflowRunResult> ExecuteAsync(WorkflowRecord rec,string? project,IServiceProvider services,CancellationToken ct)
    {
        WorkflowDocument doc;
        try { doc = WorkflowMarkdown.Parse(rec.Yaml); }
        catch (Exception ex) { return Fail("Invalid workflow YAML: " + ex.Message); }

        var err = WorkflowMarkdown.Validate(doc);
        if (err is not null) return Fail(err);

        var model = ResolveModel(doc);
        if (string.IsNullOrWhiteSpace(model))
            return Fail("No model available. Set preferred_model on the workflow or select a chat model.");

        var prompt = doc.ExecuteSkill.UserMessage;
        var convo = project==null?null:ProjectStore.ChatPrefix(project)+Guid.NewGuid().ToString("N");
        if(project!=null){var context=await services.GetRequiredService<ProjectRetrieval>().FindAsync(project,prompt??doc.Name,ct);prompt=context.Context+"\n"+(prompt??$"Run {doc.Name}.");}
        var skillResult = await _skillRunner.RunAsync(new SkillRunRequest
        {
            SkillId = doc.ExecuteSkill.Id,
            ModelId = model,
            UserMessageOverride = prompt, ConversationId=convo,
            Parameters = doc.ExecuteSkill.Parameters,
            Source = SkillRunSource.Workflow,
            WorkflowId = doc.Id,
            WorkflowName = string.IsNullOrWhiteSpace(doc.Name) ? doc.Id : doc.Name,
            TriggerDetail = $"trigger={doc.Trigger.Type}"
                + (string.IsNullOrWhiteSpace(doc.Trigger.Expression) ? "" : $" {doc.Trigger.Expression}")
        }, ct);

        if(project!=null && convo!=null && skillResult.Success){
            var store=services.GetRequiredService<ProjectStore>();var chats=services.GetRequiredService<IConversationStore>();var sync=services.GetRequiredService<ISyncService>();
            var msgId=Guid.NewGuid().ToString("N");List<ProjectSource> outputs=[];
            foreach(var a in skillResult.Attachments??[]){var f=await store.AddFileAsync(project,a.Name,a.ContentType,Convert.FromBase64String(a.DataBase64),convo,msgId);outputs.Add(new(f.Id,f.Name,1));}
            if(outputs.Count==0 && !string.IsNullOrWhiteSpace(skillResult.Text)){var f=await store.AddFileAsync(project,rec.Name+".md","text/markdown",System.Text.Encoding.UTF8.GetBytes(skillResult.Text),convo,msgId);outputs.Add(new(f.Id,f.Name,1));}
            List<ChatMessage> transcript=[new(Role:"user",Content:rec.Name,ItemId:Guid.NewGuid().ToString("N"),Timestamp:DateTime.UtcNow),new(Role:"assistant",Content:skillResult.Text??"Workflow completed.",ItemId:msgId,Timestamp:DateTime.UtcNow,ProjectOutputs:outputs)];
            await chats.SaveConversationAsync(convo,transcript,ct);await chats.UpdateIndexAfterSaveAsync(convo,transcript,await chats.LoadIndexAsync(ct),ct);sync.ScheduleAutoSyncConvoAfterLocalSave(convo);sync.ScheduleProjectSync();
        }

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
