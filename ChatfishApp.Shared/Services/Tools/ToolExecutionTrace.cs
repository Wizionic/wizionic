namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Collects structured steps during a single tool-using LLM call (for the current Send).
/// Used to display "thinking" / tool trace to the user and to feed better context if needed.
/// This is WASM-only and cleared per CallProvider invocation.
/// </summary>
public static class ToolExecutionTrace
{
    private static readonly List<string> _steps = new();

    /// <summary>Clear traces at the start of a new agentic response generation.</summary>
    public static void Clear() => _steps.Clear();

    /// <summary>Record a step in the thinking/tool process. Called from within the tool functions.</summary>
    public static void Record(string message)
    {
        _steps.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
    }

    /// <summary>Get a copy of the trace for the just-completed call.</summary>
    public static IReadOnlyList<string> GetCurrentTrace() => _steps.ToList();
}
