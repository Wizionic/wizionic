namespace App.Core.Skills;

/// <summary>One completed (or failed) skill execution for history UI.</summary>
public sealed class SkillRunLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SkillId { get; set; } = "";
    public string SkillName { get; set; } = "";
    /// <summary>Catalog model id that ran the skill (e.g. lemonade/…, ollama/…).</summary>
    public string ModelId { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ResultText { get; set; }
    /// <summary>Tool/route log lines collected during the run.</summary>
    public List<string> LogLines { get; set; } = new();
    public double DurationSeconds => Math.Max(0, (EndedAtUtc - StartedAtUtc).TotalSeconds);
}

public interface ISkillRunLogStore
{
    Task LoadAsync(CancellationToken ct = default);
    IReadOnlyList<SkillRunLog> ListRecent(int max = 40);
    Task AddAsync(SkillRunLog log, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    event Action? Changed;
}
