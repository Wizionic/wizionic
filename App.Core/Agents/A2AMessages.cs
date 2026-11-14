using System.Text.Json;
using System.Text.Json.Serialization;
using App.Core.Skills;

namespace App.Core.Agents;

/// <summary>SignalR message type for A2A frames. The hub routes this; it does not read the body.</summary>
public static class A2AChannel
{
    public const string MessageType = "a2a";
    public const string ProtocolVersion = "1.0";
}

public static class A2AMethod
{
    public const string Send = "send";
    public const string Step = "step";
    public const string Status = "status";
    public const string Feedback = "feedback";
    public const string FeedbackSaved = "feedback-saved";
    public const string Cancel = "cancel";
}

public static class A2ATaskState
{
    public const string Submitted = "submitted";
    public const string Working = "working";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

/// <summary>
/// Cleartext wrapper. <see cref="Cipher"/> is the AES-256-GCM body
/// (skill text, arguments, results, feedback notes). The hub may see task id, seq, and method.
/// </summary>
public sealed class A2AEnvelope
{
    public string TaskId { get; set; } = "";
    public int Seq { get; set; }
    public string Method { get; set; } = "";
    public string Cipher { get; set; } = "";
}

public sealed class A2ASendBody
{
    public string SkillId { get; set; } = "";
    public string SkillName { get; set; } = "";
    public string? UserMessage { get; set; }
}

public sealed class A2AStepBody
{
    public SkillStep? Step { get; set; }
}

public sealed class A2AStatusBody
{
    public string State { get; set; } = "";
    public string? Text { get; set; }
    public string? Error { get; set; }
    public string? LogId { get; set; }
}

public sealed class A2AFeedbackBody
{
    public string LogId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>
/// Read-only copy of a remote skill run, kept on the orchestrator.
/// Deleting it does not delete the worker's log.
/// </summary>
public sealed class AgentTaskReceipt
{
    public string TaskId { get; set; } = "";
    public string WorkerDeviceId { get; set; } = "";
    public string WorkerName { get; set; } = "";
    public string SkillId { get; set; } = "";
    public string SkillName { get; set; } = "";
    public string? UserMessage { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateTimeOffset LastEventUtc { get; set; }
    /// <summary><see cref="A2ATaskState"/> value.</summary>
    public string State { get; set; } = A2ATaskState.Submitted;
    /// <summary>True when the orchestrator stopped hearing steps. The worker may still be running.</summary>
    public bool Behind { get; set; }
    public string? Error { get; set; }
    public string? ResultText { get; set; }
    public string? WorkerLogId { get; set; }
    public List<SkillStep> Steps { get; set; } = new();
}

public static class A2AJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
