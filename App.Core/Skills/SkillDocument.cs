namespace App.Core.Skills;

/// <summary>
/// Parsed Agent Skills (SKILL.md) document — official frontmatter + markdown body.
/// See https://agentskills.io/specification
/// </summary>
public sealed class SkillDocument
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? License { get; set; }
    public string? Compatibility { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Space-separated pre-approved tools (official experimental field).</summary>
    public string? AllowedTools { get; set; }
    /// <summary>Markdown body after the YAML frontmatter.</summary>
    public string BodyMarkdown { get; set; } = "";

    public string? Author => GetMeta("author");
    public string? Version => GetMeta("version");
    public string? TriggerPhrases => GetMeta("trigger-phrases") ?? GetMeta("trigger_phrases");

    public IReadOnlyList<string> Tags
    {
        get
        {
            var raw = GetMeta("tags");
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    public IReadOnlyList<string> AllowedToolTokens
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AllowedTools)) return Array.Empty<string>();
            return AllowedTools.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    public string? GetMeta(string key) =>
        Metadata.TryGetValue(key, out var v) ? v : null;

    public void SetMeta(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (string.IsNullOrWhiteSpace(value))
            Metadata.Remove(key);
        else
            Metadata[key] = value;
    }
}

/// <summary>Persisted skill package (full SKILL.md text).</summary>
public sealed class SkillRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Markdown { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SkillValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
}
