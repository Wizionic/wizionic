using App.Core.Workflows;

namespace App.Shared.Services.Workflows;

public static class WorkflowSeedCatalog
{
    public static IReadOnlyList<WorkflowRecord> CreateSamples(string? preferredModel)
    {
        var model = string.IsNullOrWhiteSpace(preferredModel)
            ? "lemonade/Qwen3.5-4B-GGUF"
            : preferredModel.Trim();

        return new[]
        {
            Make(new WorkflowDocument
            {
                Id = "nightly-inspiration",
                Name = "Nightly inspiration",
                Enabled = false,
                Trigger = new WorkflowTrigger { Type = "cron", Expression = "0 21 * * *" },
                Orchestrator = new WorkflowOrchestration
                {
                    Strategy = "fallback_chain",
                    PreferredModel = model,
                    FallbackModel = model
                },
                ExecuteSkill = new WorkflowExecuteSkill
                {
                    Id = "positive-inspiration-image",
                    UserMessage = "Nightly scheduled inspiration run"
                },
                Calendar = new WorkflowCalendarProjection
                {
                    Project = true,
                    Title = "Nightly inspiration",
                    Color = "#7c3aed"
                }
            }),
            Make(new WorkflowDocument
            {
                Id = "weekday-focus-plan",
                Name = "Weekday morning plan",
                Enabled = false,
                Trigger = new WorkflowTrigger { Type = "cron", Expression = "30 8 * * 1-5" },
                Orchestrator = new WorkflowOrchestration
                {
                    Strategy = "fallback_chain",
                    PreferredModel = model
                },
                ExecuteSkill = new WorkflowExecuteSkill
                {
                    Id = "weekly-planning-block",
                    UserMessage = "Weekday morning planning"
                },
                Calendar = new WorkflowCalendarProjection
                {
                    Project = true,
                    Title = "Weekday morning plan"
                }
            })
        };
    }

    private static WorkflowRecord Make(WorkflowDocument doc) => new()
    {
        Id = doc.Id,
        Name = doc.Name,
        Yaml = WorkflowMarkdown.Serialize(doc),
        Enabled = doc.Enabled,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };
}
