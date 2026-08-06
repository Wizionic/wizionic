namespace App.Core.Tools;

/// <summary>
/// Records tool execution steps for a single chat completion turn.
/// </summary>
public interface IToolExecutionTrace
{
    void Clear();
    void Record(string message);
    IReadOnlyList<string> GetCurrentTrace();
}