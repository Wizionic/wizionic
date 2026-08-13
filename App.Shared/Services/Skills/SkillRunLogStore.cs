using System.Text.Json;
using App.Core.Skills;
using App.Core.Sync;

namespace App.Shared.Services.Skills;

/// <summary>Persists recent skill run logs in preferences (local-only).</summary>
public sealed class SkillRunLogStore : ISkillRunLogStore
{
    public const string StorageKey = "app-skill-run-logs-v1";
    private const int MaxLogs = 50;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISyncPreferencesStore _prefs;
    private readonly List<SkillRunLog> _logs = new();
    private readonly object _lock = new();

    public event Action? Changed;

    public SkillRunLogStore(ISyncPreferencesStore prefs)
    {
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var json = await _prefs.GetStringAsync(StorageKey, ct);
        lock (_lock)
        {
            _logs.Clear();
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                var list = JsonSerializer.Deserialize<List<SkillRunLog>>(json, JsonOpts);
                if (list != null)
                    _logs.AddRange(list.OrderByDescending(l => l.StartedAtUtc));
            }
            catch
            {
                // corrupt → empty
            }
        }
    }

    public IReadOnlyList<SkillRunLog> ListRecent(int max = 40)
    {
        lock (_lock)
            return _logs.OrderByDescending(l => l.StartedAtUtc).Take(Math.Max(1, max)).ToList();
    }

    public async Task AddAsync(SkillRunLog log, CancellationToken ct = default)
    {
        if (log is null) return;
        lock (_lock)
        {
            _logs.Insert(0, log);
            while (_logs.Count > MaxLogs)
                _logs.RemoveAt(_logs.Count - 1);
        }
        await PersistAsync(ct);
        Changed?.Invoke();
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        lock (_lock)
            _logs.Clear();
        await PersistAsync(ct);
        Changed?.Invoke();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        string json;
        lock (_lock)
            json = JsonSerializer.Serialize(_logs, JsonOpts);
        await _prefs.SetStringAsync(StorageKey, json, ct);
    }
}

public sealed class NullSkillRunLogStore : ISkillRunLogStore
{
    public static readonly NullSkillRunLogStore Instance = new();
    public event Action? Changed;
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<SkillRunLog> ListRecent(int max = 40) => Array.Empty<SkillRunLog>();
    public Task AddAsync(SkillRunLog log, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
}
