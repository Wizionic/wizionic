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

    public SkillRunLog? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock)
            return _logs.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal));
    }

    public Task AddAsync(SkillRunLog log, CancellationToken ct = default) => UpdateAsync(log, ct);

    public async Task UpdateAsync(SkillRunLog log, CancellationToken ct = default)
    {
        if (log is null || string.IsNullOrWhiteSpace(log.Id)) return;
        lock (_lock)
        {
            var idx = _logs.FindIndex(l => string.Equals(l.Id, log.Id, StringComparison.Ordinal));
            if (idx < 0)
                _logs.Insert(0, log);
            else
            {
                MergeFeedback(_logs[idx], log);
                _logs[idx] = log;
            }
            while (_logs.Count > MaxLogs)
                _logs.RemoveAt(_logs.Count - 1);
        }
        await PersistAsync(ct);
        Changed?.Invoke();
    }

    public async Task<bool> SetFeedbackAsync(
        string logId,
        string stepId,
        string verdict,
        string? note,
        string? by,
        CancellationToken ct = default)
    {
        if (verdict is not (SkillStepFeedbackVerdict.Positive or SkillStepFeedbackVerdict.Negative))
            return false;
        if (string.IsNullOrWhiteSpace(logId) || string.IsNullOrWhiteSpace(stepId))
            return false;

        var found = false;
        lock (_lock)
        {
            var log = _logs.FirstOrDefault(l => string.Equals(l.Id, logId, StringComparison.Ordinal));
            var step = log?.Steps?.FirstOrDefault(s => string.Equals(s.StepId, stepId, StringComparison.Ordinal));
            if (step is null)
                return false;
            step.FeedbackVerdict = verdict;
            var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (trimmed is { Length: > 500 })
                trimmed = trimmed[..500];
            step.FeedbackNote = trimmed;
            step.FeedbackBy = string.IsNullOrWhiteSpace(by) ? "user" : by.Trim();
            step.FeedbackAtUtc = DateTimeOffset.UtcNow;
            found = true;
        }

        if (!found)
            return false;
        await PersistAsync(ct);
        Changed?.Invoke();
        return true;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        lock (_lock)
            _logs.Clear();
        await PersistAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// A runner save replaces the log. Copy feedback the user already stored onto steps
    /// that the new snapshot does not mark, matched by step id.
    /// </summary>
    private static void MergeFeedback(SkillRunLog existing, SkillRunLog incoming)
    {
        if (existing.Steps is null || incoming.Steps is null)
            return;
        var prev = new Dictionary<string, SkillStep>(StringComparer.Ordinal);
        foreach (var step in existing.Steps)
        {
            if (string.IsNullOrEmpty(step.StepId) || string.IsNullOrEmpty(step.FeedbackVerdict))
                continue;
            prev[step.StepId] = step;
        }
        foreach (var step in incoming.Steps)
        {
            if (!string.IsNullOrEmpty(step.FeedbackVerdict) || string.IsNullOrEmpty(step.StepId))
                continue;
            if (!prev.TryGetValue(step.StepId, out var old))
                continue;
            step.FeedbackVerdict = old.FeedbackVerdict;
            step.FeedbackNote = old.FeedbackNote;
            step.FeedbackBy = old.FeedbackBy;
            step.FeedbackAtUtc = old.FeedbackAtUtc;
        }
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
    public SkillRunLog? Get(string id) => null;
    public Task AddAsync(SkillRunLog log, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateAsync(SkillRunLog log, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> SetFeedbackAsync(string logId, string stepId, string verdict, string? note, string? by, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
}
