namespace App.Core.Tools;

/// <summary>
/// Records tool execution steps for a single chat completion turn.
/// </summary>
public interface IToolExecutionTrace
{
    void Clear();
    void Record(string message);
    IReadOnlyList<string> GetCurrentTrace();

    /// <summary>Raised after each <see cref="Record"/> / <see cref="Clear"/> (UI live logs).</summary>
    event Action? Changed;
}