namespace App.Core.Sync;

/// <summary>
/// Skill offered by a device. Id, name, and description only — no skill body, tokens, or tool secrets.
/// </summary>
public sealed class AgentSkillCard
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>Result of publishing a card or claiming the orchestrator lease.</summary>
public readonly record struct AgentCardPublishResult(bool Ok, bool Denied);
