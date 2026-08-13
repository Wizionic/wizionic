namespace App.Core.Skills;

/// <summary>Local-only skill library (SKILL.md packages). Never stored on the central server.</summary>
public interface ISkillStore
{
    Task LoadAsync(CancellationToken ct = default);
    IReadOnlyList<SkillRecord> List();
    SkillRecord? Get(string idOrName);
    Task UpsertAsync(SkillRecord record, CancellationToken ct = default);
    Task DeleteAsync(string idOrName, CancellationToken ct = default);
    Task ReplaceAllAsync(IEnumerable<SkillRecord> records, CancellationToken ct = default);
    event Action? Changed;
}
