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
    private List<string> _steps = [];

    public event Action? Changed;

    public void Clear()
    {
        lock (_gate)
            _steps = [];
        Changed?.Invoke();
    }

    public void Record(string message)
    {
        lock (_gate)
            _steps.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
        Changed?.Invoke();
    }

    public IReadOnlyList<string> GetCurrentTrace()
    {
        lock (_gate)
            return _steps.ToList();
    }
}
