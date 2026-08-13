using System.Text.Json;
using App.Core.Skills;

namespace App.Shared.Services.Skills;

/// <summary>In-memory skill list with JSON load/save hooks for platform stores.</summary>
public abstract class SkillStoreBase : ISkillStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly List<SkillRecord> _skills = new();
    private readonly object _lock = new();

    public event Action? Changed;

    public virtual async Task LoadAsync(CancellationToken ct = default)
    {
        var json = await ReadJsonAsync(ct);
        lock (_lock)
        {
            _skills.Clear();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var list = JsonSerializer.Deserialize<List<SkillRecord>>(json, JsonOpts);
                    if (list != null)
                        _skills.AddRange(list);
                }
                catch
                {
                    // corrupt → start empty
                }
            }
        }
    }

    public IReadOnlyList<SkillRecord> List()
    {
        lock (_lock)
            return _skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public SkillRecord? Get(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        lock (_lock)
        {
            return _skills.FirstOrDefault(s =>
                s.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task UpsertAsync(SkillRecord record, CancellationToken ct = default)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        var doc = SkillMarkdown.Parse(record.Markdown ?? "");
        var name = !string.IsNullOrWhiteSpace(record.Name) ? record.Name : doc.Name;
        name = SkillMarkdown.NormalizeName(name);
        if (string.IsNullOrEmpty(name) && !string.IsNullOrWhiteSpace(doc.Name))
            name = SkillMarkdown.NormalizeName(doc.Name);

        record.Name = name;
        record.Id = string.IsNullOrWhiteSpace(record.Id) ? name : record.Id;
        if (string.IsNullOrWhiteSpace(record.Markdown) && doc.Name.Length > 0)
            record.Markdown = SkillMarkdown.Serialize(doc);
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            _skills.RemoveAll(s =>
                s.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase));
            _skills.Add(record);
        }

        await PersistAsync(ct);
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string idOrName, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _skills.RemoveAll(s =>
                s.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
        }
        await PersistAsync(ct);
        Changed?.Invoke();
    }

    public async Task ReplaceAllAsync(IEnumerable<SkillRecord> records, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _skills.Clear();
            foreach (var r in records)
            {
                if (r is null || string.IsNullOrWhiteSpace(r.Markdown)) continue;
                _skills.Add(r);
            }
        }
        await PersistAsync(ct);
        Changed?.Invoke();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        string json;
        lock (_lock)
            json = JsonSerializer.Serialize(_skills, JsonOpts);
        await WriteJsonAsync(json, ct);
    }

    protected abstract Task<string?> ReadJsonAsync(CancellationToken ct);
    protected abstract Task WriteJsonAsync(string json, CancellationToken ct);
}
