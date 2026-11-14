namespace App.Core.Skills;

/// <summary>Kind of one structured skill-run step.</summary>
public static class SkillStepKind
{
    public const string Route = "route";
    public const string Model = "model";
    public const string ToolCall = "tool-call";
    public const string ToolResult = "tool-result";
    public const string Assistant = "assistant";
    public const string Error = "error";
    public const string Note = "note";
}

/// <summary>Outcome of a step. Running steps can still receive later feedback once they finish.</summary>
public static class SkillStepStatus
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>User mark on a finished tool step. Stored on the worker next to that step.</summary>
public static class SkillStepFeedbackVerdict
{
    public const string Positive = "positive";
    public const string Negative = "negative";
}

/// <summary>
/// One visible step in a skill run. <see cref="Text"/> is the line the old string log showed,
/// so a step can render even when older code only stored sentences.
/// </summary>
public sealed class SkillStep
{
    public string StepId { get; set; } = Guid.NewGuid().ToString("N");
    public int Index { get; set; }
    public string Kind { get; set; } = SkillStepKind.Note;
    public string? ToolName { get; set; }
    public string? ArgsSummary { get; set; }
    public string Status { get; set; } = SkillStepStatus.Succeeded;
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string Text { get; set; } = "";
    public string? FeedbackVerdict { get; set; }
    public string? FeedbackNote { get; set; }
    public string? FeedbackBy { get; set; }
    public DateTimeOffset? FeedbackAtUtc { get; set; }

    public SkillStep Clone() => new()
    {
        StepId = StepId,
        Index = Index,
        Kind = Kind,
        ToolName = ToolName,
        ArgsSummary = ArgsSummary,
        Status = Status,
        StartedAtUtc = StartedAtUtc,
        EndedAtUtc = EndedAtUtc,
        Text = Text,
        FeedbackVerdict = FeedbackVerdict,
        FeedbackNote = FeedbackNote,
        FeedbackBy = FeedbackBy,
        FeedbackAtUtc = FeedbackAtUtc
    };
}

/// <summary>Payload from the run panel when the user marks a step.</summary>
public sealed class SkillStepFeedback
{
    public string StepId { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string? Note { get; set; }
}
