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

        var system = BuildSkillSystemPrompt(doc, body, request.Parameters, resolution, modelId);
        var userMsg = string.IsNullOrWhiteSpace(request.UserMessageOverride)
            ? $"Execute the skill `{doc.Name}` now. Follow every step completely and use the available tools."
            : request.UserMessageOverride!.Trim();

        var started = DateTimeOffset.UtcNow;
        _trace.Clear();
        void PushLog()
        {
            try { request.OnLog?.Invoke(_trace.GetCurrentTrace()); }
            catch { /* UI may be gone */ }
        }

        void OnTraceChanged() => PushLog();
        _trace.Changed += OnTraceChanged;

        _trace.Record($"🎯 Skill run: {doc.Name}");
        _trace.Record($"🤖 Model: {modelId}");
        _trace.Record($"🔧 Modules: [{string.Join(", ", modules)}] · MCP={resolution.IncludeMcp}");
        _trace.Record("📜 Loading skill instructions…");
        _trace.Record("▶ Starting model + tools…");

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
                // Ensure full tooltrace is captured if not already in live steps
                foreach (var line in result.ToolTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!logLines.Any(l => l.Contains(line.Trim(), StringComparison.Ordinal)))
                        logLines.Add(line);
                }
            }

            var ended = DateTimeOffset.UtcNow;
            var log = new SkillRunLog
            {
                SkillId = record.Id,
                SkillName = doc.Name,
                ModelId = modelId,
                StartedAtUtc = started,
                EndedAtUtc = ended,
                Success = string.IsNullOrWhiteSpace(result.Error),
                Error = result.Error,
                ResultText = result.Text,
                LogLines = logLines
            };

            if (_runLogs is not null)
            {
                try { await _runLogs.AddAsync(log, ct); }
                catch { /* non-fatal */ }
            }

            _trace.Record(log.Success
                ? $"✅ Skill finished in {log.DurationSeconds:0.0}s · model {modelId}"
                : $"❌ Skill failed · model {modelId}: {result.Error}");
            PushLog();

            outcome = new SkillRunResult
            {
                Success = log.Success,
                Text = result.Text,
                ToolTrace = string.Join("\n", logLines),
                Error = result.Error,
                ConversationId = request.ConversationId,
                ModelId = modelId,
                Log = log
            };
        }
        catch (Exception ex)
        {
            _trace.Record("❌ " + ex.Message);
            PushLog();
            var log = new SkillRunLog
            {
                SkillId = record.Id,
                SkillName = doc.Name,
                ModelId = modelId,
                StartedAtUtc = started,
                EndedAtUtc = DateTimeOffset.UtcNow,
                Success = false,
                Error = ex.Message,
                LogLines = _trace.GetCurrentTrace().ToList()
            };
            if (_runLogs is not null)
            {
                try { await _runLogs.AddAsync(log, ct); }
                catch { /* ignore */ }
            }
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

    private static string BuildSkillSystemPrompt(
        SkillDocument doc,
        string body,
        Dictionary<string, string>? parameters,
        SkillToolResolver.Resolution resolution,
        string modelId)
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
