using App.Core.Skills;

namespace App.Core.Tools;

/// <summary>
/// Records tool execution steps for a single chat completion turn.
/// </summary>
public interface IToolExecutionTrace
{
    void Clear();
    void Record(string message);
    /// <summary>
    /// Append a structured step and its display line.
    /// Do not also <see cref="Record"/> the same tool call — that would store it twice.
    /// </summary>
    void RecordStep(SkillStep step);
    IReadOnlyList<string> GetCurrentTrace();
    IReadOnlyList<SkillStep> GetSteps();

    /// <summary>Raised after each <see cref="Record"/> / <see cref="RecordStep"/> / <see cref="Clear"/> (UI live logs).</summary>
    event Action? Changed;
}