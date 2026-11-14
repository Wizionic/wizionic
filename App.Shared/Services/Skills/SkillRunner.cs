using System.Text;
using App.Core.Chat;
using App.Core.Skills;
using App.Core.Storage;
using App.Core.Tools;
using App.Shared.Services.Tools;
using StoreChatMessage = App.Core.Storage.ChatMessage;

namespace App.Shared.Services.Skills;

/// <summary>
/// Executes a SKILL.md by forcing tool modules and injecting skill instructions into chat completion.
/// Streams tool-trace log lines via <see cref="SkillRunRequest.OnLog"/> and persists a run log.
/// </summary>
public sealed class SkillRunner : ISkillRunner
{
    private readonly ISkillStore _skills;
    private readonly IChatCompletionService _chat;
    private readonly IKeyStore _keyStore;
    private readonly IToolProvider _tools;
    private readonly IToolExecutionTrace _trace;
    private readonly ISkillRunLogStore? _runLogs;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private int _persistGen;
    private int _appliedGen;

    public SkillRunner(
        ISkillStore skills,
        IChatCompletionService chat,
        IKeyStore keyStore,
        IToolProvider tools,
        IToolExecutionTrace trace,
        ISkillRunLogStore? runLogs = null)
    {
        _skills = skills;
        _chat = chat;
        _keyStore = keyStore;
        _tools = tools;
        _trace = trace;
        _runLogs = runLogs;
    }

    public async Task<SkillRunResult> RunAsync(SkillRunRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SkillId))
            return Fail("No skill specified.");

        await _skills.LoadAsync(ct);
        if (_runLogs is not null)
        {
            try { await _runLogs.LoadAsync(ct); }
            catch { /* history is optional */ }
        }

        var record = _skills.Get(request.SkillId);
        if (record is null)
            return Fail($"Skill '{request.SkillId}' not found.");

        if (!record.Enabled)
            return Fail($"Skill '{record.Name}' is disabled.");

        SkillDocument doc;
        try
        {
            doc = SkillMarkdown.Parse(record.Markdown);
        }
        catch (Exception ex)
        {
            return Fail("Invalid SKILL.md: " + ex.Message);
        }

        var validation = SkillMarkdown.Validate(doc);
        if (!validation.IsValid)
            return Fail(string.Join(" ", validation.Errors));

        var modelId = (request.ModelId ?? _keyStore.LastSelectedModel ?? "").Trim();
        if (string.IsNullOrEmpty(modelId))
            return Fail("No chat model selected. Pick a model on the Chat page first.");

        var resolution = SkillToolResolver.Resolve(doc.AllowedTools);
        var active = _tools.GetActiveModules().Select(m => m.ModuleName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modules = resolution.Modules
            .Where(m => active.Contains(m) || string.Equals(m, "Native", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (modules.Count == 0)
            modules = resolution.Modules.ToList();

        var body = !string.IsNullOrWhiteSpace(request.BodyOverride)
            ? request.BodyOverride!
            : doc.BodyMarkdown;

        var prior = SkillPriorOutcomes.Format(_runLogs, record.Id);
        var system = BuildSkillSystemPrompt(doc, body, request.Parameters, resolution, modelId, prior);
        var userMsg = string.IsNullOrWhiteSpace(request.UserMessageOverride)
            ? $"Execute the skill `{doc.Name}` now. Follow every step completely and use the available tools."
            : request.UserMessageOverride!.Trim();

        var started = DateTimeOffset.UtcNow;
        var source = string.IsNullOrWhiteSpace(request.Source) ? SkillRunSource.Manual : request.Source.Trim();
        var log = new SkillRunLog
        {
            SkillId = record.Id,
            SkillName = doc.Name,
            ModelId = modelId,
            Source = source,
            WorkflowId = request.WorkflowId,
            WorkflowName = request.WorkflowName,
            TriggerDetail = request.TriggerDetail,
            TaskId = request.TaskId,
            State = SkillRunState.Running,
            StartedAtUtc = started,
            EndedAtUtc = started,
            Success = false
        };

        _trace.Clear();
        void Push()
        {
            log.LogLines = _trace.GetCurrentTrace().ToList();
            log.Steps = _trace.GetSteps().Select(s => s.Clone()).ToList();
            try { request.OnLog?.Invoke(log.LogLines); }
            catch { /* UI may be gone */ }
            try { request.OnSteps?.Invoke(log.Steps); }
            catch { /* UI may be gone */ }
            SchedulePersist(log);
        }

        void OnTraceChanged() => Push();
        _trace.Changed += OnTraceChanged;

        _trace.Record($"🎯 Skill run: {doc.Name}");
        _trace.Record($"📥 Source: {source}"
            + (string.IsNullOrWhiteSpace(request.WorkflowId) ? "" : $" · workflow={request.WorkflowId}")
            + (string.IsNullOrWhiteSpace(request.TriggerDetail) ? "" : $" · {request.TriggerDetail}"));
        _trace.Record($"🤖 Model: {modelId}");
        _trace.Record($"🔧 Modules: [{string.Join(", ", modules)}] · MCP={resolution.IncludeMcp}");
        _trace.Record("📜 Loading skill instructions…");
        _trace.Record("▶ Starting model + tools…");

        // The id has to be on disk before the model runs so a mid-run note can find the step.
        await PersistNowAsync(log);
        try { request.OnRunStarted?.Invoke(log.Id); }
        catch { /* listener is optional */ }

        SkillExecutionContext.Current = new SkillExecutionContext
        {
            SkillName = doc.Name,
            SystemInstructions = system,
            Modules = modules,
            IncludeMcp = resolution.IncludeMcp,
            ModelId = modelId
        };

        SkillRunResult outcome;
        try
        {
            var messages = new List<StoreChatMessage>
            {
                new(Role: "user", Content: userMsg, Timestamp: DateTime.UtcNow)
            };

            var result = await _chat.CompleteAsync(
                modelId,
                messages,
                currentUser: null,
                conversationId: request.ConversationId,
                ct: ct,
                onPartialText: async text =>
                {
                    if (request.OnPartialText is not null)
                        await request.OnPartialText(text);
                });

            var logLines = _trace.GetCurrentTrace().ToList();
            if (!string.IsNullOrWhiteSpace(result.ToolTrace))
            {
                foreach (var line in result.ToolTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!logLines.Any(l => l.Contains(line.Trim(), StringComparison.Ordinal)))
                        logLines.Add(line);
                }
            }

            log.LogLines = logLines;
            log.Steps = _trace.GetSteps().Select(s => s.Clone()).ToList();
            log.EndedAtUtc = DateTimeOffset.UtcNow;
            log.Success = string.IsNullOrWhiteSpace(result.Error);
            log.Error = result.Error;
            log.ResultText = result.Text;
            log.State = log.Success ? SkillRunState.Completed : SkillRunState.Failed;

            _trace.Record(log.Success
                ? $"✅ Skill finished in {log.DurationSeconds:0.0}s · model {modelId} · source={source}"
                : $"❌ Skill failed · model {modelId}: {result.Error}");
            log.LogLines = _trace.GetCurrentTrace().ToList();
            log.Steps = _trace.GetSteps().Select(s => s.Clone()).ToList();
            await PersistNowAsync(log);
            try { request.OnLog?.Invoke(log.LogLines); }
            catch { /* UI may be gone */ }
            try { request.OnSteps?.Invoke(log.Steps); }
            catch { /* UI may be gone */ }

            outcome = new SkillRunResult
            {
                Success = log.Success,
                Text = result.Text,
                ToolTrace = string.Join("\n", log.LogLines),
                Error = result.Error,
                ConversationId = request.ConversationId,
                ModelId = modelId,
                Log = log
            };
        }
        catch (OperationCanceledException)
        {
            _trace.Record("❌ Canceled");
            log.EndedAtUtc = DateTimeOffset.UtcNow;
            log.Success = false;
            log.Error = "Canceled";
            log.State = SkillRunState.Canceled;
            log.LogLines = _trace.GetCurrentTrace().ToList();
            log.Steps = _trace.GetSteps().Select(s => s.Clone()).ToList();
            await PersistNowAsync(log);
            try { request.OnSteps?.Invoke(log.Steps); }
            catch { /* UI may be gone */ }
            outcome = new SkillRunResult
            {
                Success = false,
                Error = "Canceled",
                ToolTrace = string.Join("\n", log.LogLines),
                ModelId = modelId,
                Log = log
            };
        }
        catch (Exception ex)
        {
            _trace.Record("❌ " + ex.Message);
            log.EndedAtUtc = DateTimeOffset.UtcNow;
            log.Success = false;
            log.Error = ex.Message;
            log.State = SkillRunState.Failed;
            log.LogLines = _trace.GetCurrentTrace().ToList();
            log.Steps = _trace.GetSteps().Select(s => s.Clone()).ToList();
            await PersistNowAsync(log);
            try { request.OnSteps?.Invoke(log.Steps); }
            catch { /* UI may be gone */ }
            outcome = new SkillRunResult
            {
                Success = false,
                Error = ex.Message,
                ToolTrace = string.Join("\n", log.LogLines),
                ModelId = modelId,
                Log = log
            };
        }
        finally
        {
            _trace.Changed -= OnTraceChanged;
            SkillExecutionContext.Current = null;
        }

        return outcome;
    }

    private void SchedulePersist(SkillRunLog log)
    {
        if (_runLogs is null) return;
        var gen = Interlocked.Increment(ref _persistGen);
        var snap = CloneLog(log);
        _ = PersistSequentialAsync(snap, gen);
    }

    private async Task PersistNowAsync(SkillRunLog log)
    {
        if (_runLogs is null) return;
        var gen = Interlocked.Increment(ref _persistGen);
        await PersistSequentialAsync(CloneLog(log), gen);
    }

    /// <summary>
    /// Writes are serialized. A snapshot taken earlier than one already stored is skipped,
    /// so a slow mid-run save cannot replace the finished log.
    /// </summary>
    private async Task PersistSequentialAsync(SkillRunLog snap, int gen)
    {
        await _persistGate.WaitAsync();
        try
        {
            if (gen < Volatile.Read(ref _appliedGen))
                return;
            Volatile.Write(ref _appliedGen, gen);
            await _runLogs!.UpdateAsync(snap);
        }
        catch { /* non-fatal */ }
        finally { _persistGate.Release(); }
    }

    private static SkillRunLog CloneLog(SkillRunLog log) => new()
    {
        Id = log.Id,
        SkillId = log.SkillId,
        SkillName = log.SkillName,
        ModelId = log.ModelId,
        Source = log.Source,
        WorkflowId = log.WorkflowId,
        WorkflowName = log.WorkflowName,
        TriggerDetail = log.TriggerDetail,
        TaskId = log.TaskId,
        State = log.State,
        StartedAtUtc = log.StartedAtUtc,
        EndedAtUtc = log.EndedAtUtc,
        Success = log.Success,
        Error = log.Error,
        ResultText = log.ResultText,
        LogLines = log.LogLines?.ToList() ?? new(),
        Steps = log.Steps?.Select(s => s.Clone()).ToList() ?? new()
    };

    private static string BuildSkillSystemPrompt(
        SkillDocument doc,
        string body,
        Dictionary<string, string>? parameters,
        SkillToolResolver.Resolution resolution,
        string modelId,
        string priorOutcomes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are executing the Agent Skill `{doc.Name}`.");
        sb.AppendLine($"Active model: {modelId}");
        sb.AppendLine("Follow the skill instructions exactly. Prefer real tool calls over claiming success without tools.");
        sb.AppendLine("As you work, use tools for each concrete step (gallery, notes, calendar, HA, MCP, etc.).");
        sb.AppendLine($"Skill description: {doc.Description}");
        if (resolution.Modules.Count > 0)
            sb.AppendLine("Preferred tool modules: " + string.Join(", ", resolution.Modules));
        if (resolution.IncludeMcp)
            sb.AppendLine("MCP and OAuth connector tools may also be available.");
        sb.AppendLine();
        if (parameters is { Count: > 0 })
        {
            sb.AppendLine("## Run parameters");
            foreach (var kv in parameters)
                sb.Append("- **").Append(kv.Key).Append("**: ").AppendLine(kv.Value ?? "");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(priorOutcomes))
            sb.Append(priorOutcomes);
        sb.AppendLine("## Skill instructions");
        sb.AppendLine();
        sb.Append(body?.Trim() ?? "");
        return sb.ToString();
    }

    private static SkillRunResult Fail(string error) => new()
    {
        Success = false,
        Error = error
    };
}

/// <summary>
/// Ambient skill run context consumed by <see cref="ChatCompletionService"/> for one turn.
/// AsyncLocal so concurrent chats do not cross-contaminate.
/// </summary>
public sealed class SkillExecutionContext
{
    private static readonly AsyncLocal<SkillExecutionContext?> _current = new();

    public static SkillExecutionContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public string SkillName { get; set; } = "";
    public string SystemInstructions { get; set; } = "";
    public IReadOnlyList<string> Modules { get; set; } = Array.Empty<string>();
    public bool IncludeMcp { get; set; }
    public string ModelId { get; set; } = "";
}
