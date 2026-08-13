using App.Core.Skills;

namespace App.Shared.Services.Skills;

public sealed class NullSkillStore : ISkillStore
{
    public static readonly NullSkillStore Instance = new();

    public event Action? Changed;

    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<SkillRecord> List() => Array.Empty<SkillRecord>();
    public SkillRecord? Get(string idOrName) => null;
    public Task UpsertAsync(SkillRecord record, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string idOrName, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReplaceAllAsync(IEnumerable<SkillRecord> records, CancellationToken ct = default) => Task.CompletedTask;
}
