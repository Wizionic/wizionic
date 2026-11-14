using App.Core.Skills;
using App.Core.Tools;

namespace App.Shared.Services.Tools;

/// <summary>
/// In-memory tool execution log for the current completion.
/// Uses instance storage (not AsyncLocal) so the type loads on Blazor WebAssembly —
/// AsyncLocal&lt;T&gt; fails to resolve from System.Threading in the browser runtime and
/// aborts WASM bootstrap (empty app-body / topbar-only UI).
/// Register as scoped with ChatCompletionService, or accept interleaved traces if
/// concurrent completions share a singleton.
/// </summary>
public sealed class ToolExecutionTrace : IToolExecutionTrace
{
    private readonly object _gate = new();
    private List<string> _lines = [];
    private List<SkillStep> _steps = [];

    public event Action? Changed;

    public void Clear()
    {
        lock (_gate)
        {
            _lines = [];
            _steps = [];
        }
        Changed?.Invoke();
    }

    public void Record(string message)
    {
        var text = Stamp(message);
        var step = Infer(message, text);
        lock (_gate)
        {
            step.Index = _steps.Count;
            _steps.Add(step);
            _lines.Add(text);
        }
        Changed?.Invoke();
    }

    public void RecordStep(SkillStep step)
    {
        if (step is null) return;
        if (string.IsNullOrEmpty(step.StepId))
            step.StepId = Guid.NewGuid().ToString("N");
        var body = string.IsNullOrWhiteSpace(step.Text)
            ? $"{step.Kind} {step.ToolName}".Trim()
            : step.Text.Trim();
        step.Text = body.StartsWith('[') ? body : Stamp(body);
        if (step.Text.Length > 2000)
            step.Text = step.Text[..2000] + "…";
        if (step.StartedAtUtc == default)
            step.StartedAtUtc = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            step.Index = _steps.Count;
            _steps.Add(step);
            _lines.Add(step.Text);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<string> GetCurrentTrace()
    {
        lock (_gate)
            return _lines.ToList();
    }

    public IReadOnlyList<SkillStep> GetSteps()
    {
        lock (_gate)
            return _steps.ToList();
    }

    private static string Stamp(string message) => $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}";

    private static SkillStep Infer(string message, string text)
    {
        var kind = SkillStepKind.Note;
        var status = SkillStepStatus.Succeeded;
        if (message.Contains("Route:", StringComparison.OrdinalIgnoreCase))
            kind = SkillStepKind.Route;
        else if (message.Contains("❌", StringComparison.Ordinal)
                 || message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            kind = SkillStepKind.Error;
            status = SkillStepStatus.Failed;
        }

        var now = DateTimeOffset.UtcNow;
        return new SkillStep
        {
            Kind = kind,
            Status = status,
            Text = text,
            StartedAtUtc = now,
            EndedAtUtc = now
        };
    }
}
