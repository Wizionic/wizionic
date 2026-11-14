using System.Text;
using App.Core.Skills;

namespace App.Shared.Services.Skills;

/// <summary>
/// Builds the "Prior outcomes" block for the next run of a skill.
/// Does not rewrite SKILL.md. Positive means call the tool again; negative means verify.
/// </summary>
public static class SkillPriorOutcomes
{
    public const int MaxNotes = 8;
    public const int MaxPerTool = 2;

    public static string Format(ISkillRunLogStore? logs, string skillId)
    {
        if (logs is null || string.IsNullOrWhiteSpace(skillId))
            return "";

        var notes = new List<(string Tool, string Args, string Verdict, string Note, DateTimeOffset When)>();
        foreach (var log in logs.ListRecent(50))
        {
            if (!string.Equals(log.SkillId, skillId, StringComparison.OrdinalIgnoreCase) || log.Steps is null)
                continue;
            foreach (var step in log.Steps)
            {
                if (string.IsNullOrEmpty(step.FeedbackVerdict))
                    continue;
                if (step.Kind is not (SkillStepKind.ToolCall or SkillStepKind.ToolResult))
                    continue;
                var tool = string.IsNullOrWhiteSpace(step.ToolName) ? "tool" : step.ToolName.Trim();
                notes.Add((
                    tool,
                    step.ArgsSummary ?? "",
                    step.FeedbackVerdict,
                    step.FeedbackNote ?? "",
                    step.FeedbackAtUtc ?? step.StartedAtUtc));
            }
        }

        if (notes.Count == 0)
            return "";

        var picked = new List<(string Tool, string Args, string Verdict, string Note, DateTimeOffset When)>();
        var perTool = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes.OrderByDescending(n => n.When))
        {
            if (picked.Count >= MaxNotes)
                break;
            perTool.TryGetValue(note.Tool, out var count);
            if (count >= MaxPerTool)
                continue;
            perTool[note.Tool] = count + 1;
            picked.Add(note);
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Prior outcomes");
        sb.AppendLine("These notes are from the user about earlier runs of this skill. They are not permission to skip a tool.");
        foreach (var note in picked)
        {
            var args = string.IsNullOrWhiteSpace(note.Args) ? "" : $" ({Trim(note.Args, 120)})";
            if (string.Equals(note.Verdict, SkillStepFeedbackVerdict.Positive, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("- ").Append(note.Tool).Append(args)
                    .AppendLine(": the user confirmed this worked. Call it again when the step needs it. Do not skip the tool or invent the result.");
                if (!string.IsNullOrWhiteSpace(note.Note))
                    sb.Append("  User note: ").AppendLine(Trim(note.Note, 300));
            }
            else
            {
                sb.Append("- ").Append(note.Tool).Append(args)
                    .Append(": the trace said success but the user said it failed. ");
                if (!string.IsNullOrWhiteSpace(note.Note))
                    sb.Append(Trim(note.Note, 300)).Append(' ');
                sb.AppendLine("Verify the outcome or choose another step. Do not claim success from the old trace.");
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
