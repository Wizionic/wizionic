using System.Text.Json;
using App.Core.Sync;
using App.Core.Workflows;

namespace App.Shared.Services.Workflows;

public sealed class PreferencesWorkflowStore : IWorkflowStore
{
    public const string StorageKey = "app-workflows-library-v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISyncPreferencesStore _prefs;
    private readonly List<WorkflowRecord> _items = new();
    private readonly object _lock = new();

    public event Action? Changed;

    public PreferencesWorkflowStore(ISyncPreferencesStore prefs)
    {
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var json = await _prefs.GetStringAsync(StorageKey, ct);
        lock (_lock)
        {
            _items.Clear();
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                var list = JsonSerializer.Deserialize<List<WorkflowRecord>>(json, JsonOpts);
                if (list != null) _items.AddRange(list);
            }
            catch { /* corrupt */ }
        }
    }

    public IReadOnlyList<WorkflowRecord> List()
    {
        lock (_lock)
            return _items.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public WorkflowRecord? Get(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        lock (_lock)
            return _items.FirstOrDefault(w =>
                w.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
                w.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertAsync(WorkflowRecord record, CancellationToken ct = default)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        var doc = WorkflowMarkdown.Parse(record.Yaml ?? "");
        var id = WorkflowMarkdown.NormalizeId(
            !string.IsNullOrWhiteSpace(record.Id) ? record.Id : doc.Id);
        if (string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(doc.Id))
            id = doc.Id;
        record.Id = id;
        record.Name = string.IsNullOrWhiteSpace(record.Name)
            ? (string.IsNullOrWhiteSpace(doc.Name) ? id : doc.Name)
            : record.Name;
        if (string.IsNullOrWhiteSpace(record.Yaml))
            record.Yaml = WorkflowMarkdown.Serialize(doc);
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            _items.RemoveAll(w => w.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
            _items.Add(record);
        }
        await PersistAsync(ct);
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string idOrName, CancellationToken ct = default)
    {
        lock (_lock)
            _items.RemoveAll(w =>
                w.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
                w.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
        await PersistAsync(ct);
        Changed?.Invoke();
    }

    public async Task ReplaceAllAsync(IEnumerable<WorkflowRecord> records, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _items.Clear();
            foreach (var r in records)
            {
                if (r is null || string.IsNullOrWhiteSpace(r.Yaml)) continue;
                _items.Add(r);
            }
        }
        await PersistAsync(ct);
        Changed?.Invoke();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        string json;
        lock (_lock)
            json = JsonSerializer.Serialize(_items, JsonOpts);
        await _prefs.SetStringAsync(StorageKey, json, ct);
    }
}

public sealed class NullWorkflowStore : IWorkflowStore
{
    public static readonly NullWorkflowStore Instance = new();
    public event Action? Changed;
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<WorkflowRecord> List() => Array.Empty<WorkflowRecord>();
    public WorkflowRecord? Get(string idOrName) => null;
    public Task UpsertAsync(WorkflowRecord record, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string idOrName, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReplaceAllAsync(IEnumerable<WorkflowRecord> records, CancellationToken ct = default) => Task.CompletedTask;
}
