using ChatfishApp.Core.Tools;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Per-async-context tool trace using AsyncLocal so it works with both
/// scoped (WASM) and singleton (MAUI) ChatCompletionService lifetimes.
/// </summary>
public sealed class ToolExecutionTrace : IToolExecutionTrace
{
    private static readonly AsyncLocal<List<string>?> _steps = new();

    private List<string> Steps => _steps.Value ??= new();

    public void Clear() => Steps.Clear();

    public void Record(string message) =>
        Steps.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");

    public IReadOnlyList<string> GetCurrentTrace() => Steps.ToList();
}