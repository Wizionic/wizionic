using App.Core.Auth;
using App.Core.Storage;
using System.Text.Json;

namespace App.Maui.Services;

public class SqliteCalendarStore : ICalendarStore
{
    private const string CalPrefix = "c-wasmchat-cal-";
    private const string EvtPrefix = "c-wasmchat-evt-";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly IAuthService _auth;
    private readonly ICryptoService _crypto;
    private readonly SqliteHistoryDatabase _db;

    public SqliteCalendarStore(IAuthService auth, ICryptoService crypto, SqliteHistoryDatabase db)
    {
        _auth = auth;
        _crypto = crypto;
        _db = db;
    }

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);
    private async Task<string> GetEncKeyAsync() => await _auth.GetOrCreateHistoryEncryptionKeyAsync();
    private string CalKey(string ns, string id) => ns + CalPrefix + id;
    private string EvtMetaKey(string ns, string id) => ns + EvtPrefix + id;
    private string EvtContentKey(string ns, string id) => ns + EvtPrefix + "c-" + id;

    public async Task<List<LocalCalendar>> LoadCalendarsAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetCalendarMetasByNamespaceAsync(ns, ct);
        return metas
            .Where(m => string.IsNullOrEmpty(m.DeletedAt))
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new LocalCalendar(
                m.Id,
                string.IsNullOrWhiteSpace(m.Name) ? "(empty)" : m.Name,
                string.IsNullOrWhiteSpace(m.Color) ? CalendarConstants.DefaultCalendarColor : m.Color,
                DateTime.Parse(m.LastUpdated),
                m.Description,
                m.TimeZone,
                m.IsVisible,
                m.SortOrder,
                m.IsWorkflowCalendar))
            .ToList();
    }

    public async Task EnsureDefaultCalendarAsync(CancellationToken ct = default)
    {
        var list = await LoadCalendarsAsync(ct);
        if (list.Count > 0) return;
        await CreateCalendarAsync(
            Guid.NewGuid().ToString("N"),
            CalendarConstants.DefaultCalendarName,
            CalendarConstants.DefaultCalendarColor,
            null,
            ct);
    }

    public async Task CreateCalendarAsync(string id, string name, string color, string? description = null, CancellationToken ct = default, bool isWorkflowCalendar = false)
    {
        var ns = GetPrefix();
        var now = DateTime.UtcNow;
        var existing = await LoadCalendarsAsync(ct);
        var sort = existing.Count == 0 ? 0 : existing.Max(c => c.SortOrder) + 1;
        var fp = SyncFingerprint.ForCalendar(id, name, color, true, description, now.Ticks);
        await _db.UpsertCalendarMetaAsync(new SqliteHistoryDatabase.CalendarMetaRow(
            CalKey(ns, id), id, ns, name, color, now.ToString("o"),
            _auth.IsAuthenticated, fp, null, description, null, true, sort, isWorkflowCalendar), ct);
    }

    public async Task<string> EnsureWorkflowCalendarAsync(CancellationToken ct = default)
    {
        var list = await LoadCalendarsAsync(ct);
        var existing = list.FirstOrDefault(c => c.IsWorkflowCalendar
            || c.Id.Equals(CalendarConstants.WorkflowCalendarId, StringComparison.OrdinalIgnoreCase)
            || c.Name.Equals(CalendarConstants.WorkflowCalendarName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!existing.IsWorkflowCalendar)
            {
                var ns = GetPrefix();
                var row = await _db.GetCalendarMetaByIdAsync(ns, existing.Id, ct);
                if (row is not null && string.IsNullOrEmpty(row.DeletedAt))
                {
                    var now = DateTime.UtcNow;
                    var fp = SyncFingerprint.ForCalendar(
                        existing.Id, CalendarConstants.WorkflowCalendarName, CalendarConstants.WorkflowCalendarColor,
                        existing.IsVisible, "AI skill schedules (Wizionic workflows)", now.Ticks);
                    await _db.UpsertCalendarMetaAsync(row with
                    {
                        Name = CalendarConstants.WorkflowCalendarName,
                        Color = CalendarConstants.WorkflowCalendarColor,
                        Description = "AI skill schedules (Wizionic workflows)",
                        IsWorkflowCalendar = true,
                        LastUpdated = now.ToString("o"),
                        ContentFingerprint = fp
                    }, ct);
                }
            }
            return existing.Id;
        }

        var id = CalendarConstants.WorkflowCalendarId;
        await CreateCalendarAsync(
            id,
            CalendarConstants.WorkflowCalendarName,
            CalendarConstants.WorkflowCalendarColor,
            "AI skill schedules (Wizionic workflows)",
            ct,
            isWorkflowCalendar: true);
        return id;
    }

    public async Task UpdateCalendarAsync(string id, string name, string color, bool isVisible, string? description = null, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var existing = await _db.GetCalendarMetaByIdAsync(ns, id, ct);
        if (existing is null || !string.IsNullOrEmpty(existing.DeletedAt)) return;
        var now = DateTime.UtcNow;
        var fp = SyncFingerprint.ForCalendar(id, name, color, isVisible, description, now.Ticks);
        await _db.UpsertCalendarMetaAsync(existing with
        {
            Name = name,
            Color = color,
            IsVisible = isVisible,
            Description = description,
            LastUpdated = now.ToString("o"),
            ContentFingerprint = fp
        }, ct);
    }

    public async Task SetCalendarVisibleAsync(string id, bool isVisible, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var existing = await _db.GetCalendarMetaByIdAsync(ns, id, ct);
        if (existing is null || !string.IsNullOrEmpty(existing.DeletedAt)) return;
        await UpdateCalendarAsync(id, existing.Name, existing.Color, isVisible, existing.Description, ct);
    }

    public async Task ReorderCalendarsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var existing = await _db.GetCalendarMetaByIdAsync(ns, orderedIds[i], ct);
            if (existing is null || !string.IsNullOrEmpty(existing.DeletedAt)) continue;
            await _db.UpsertCalendarMetaAsync(existing with { SortOrder = i }, ct);
        }
    }

    public async Task<DateTime> DeleteCalendarAsync(string id, CancellationToken ct = default)
    {
        var deletedAt = DateTime.UtcNow;
        var ns = GetPrefix();
        var existing = await _db.GetCalendarMetaByIdAsync(ns, id, ct);
        if (existing is null) return deletedAt;
        await _db.UpsertCalendarMetaAsync(existing with
        {
            DeletedAt = deletedAt.ToString("o"),
            LastUpdated = deletedAt.ToString("o"),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAt.Ticks)
        }, ct);
        var events = await LoadEventsForCalendarAsync(id, ct);
        foreach (var e in events)
            await SoftDeleteEventAsync(e.Id, ct);
        return deletedAt;
    }

    public async Task<List<SyncManifestEntry>> LoadCalendarManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetCalendarMetasByNamespaceAsync(ns, ct);
        var entries = new List<SyncManifestEntry>();
        foreach (var m in metas)
        {
            long? delTicks = string.IsNullOrEmpty(m.DeletedAt) ? null : DateTime.Parse(m.DeletedAt).Ticks;
            var fp = delTicks.HasValue
                ? DeleteSyncPayload.AckValue(delTicks.Value)
                : m.ContentFingerprint ?? "";
            if (!delTicks.HasValue && string.IsNullOrEmpty(fp) && backfillMissingFingerprints)
            {
                var last = DateTime.Parse(m.LastUpdated);
                fp = SyncFingerprint.ForCalendar(m.Id, m.Name, m.Color, m.IsVisible, m.Description, last.Ticks);
            }
            entries.Add(new SyncManifestEntry(m.Id, m.Name, DateTime.Parse(m.LastUpdated).Ticks, fp, delTicks));
        }
        return entries;
    }

    public async Task<bool> ShouldAcceptIncomingCalendarAsync(string id, long remoteLastUpdatedTicks, CancellationToken ct = default)
    {
        var existing = await _db.GetCalendarMetaByIdAsync(GetPrefix(), id, ct);
        if (existing is null) return true;
        return remoteLastUpdatedTicks > DateTime.Parse(existing.LastUpdated).Ticks;
    }

    public async Task<bool> TryApplyRemoteCalendarDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var existing = await _db.GetCalendarMetaByIdAsync(ns, id, ct);
        var del = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        if (existing is null)
        {
            await _db.UpsertCalendarMetaAsync(new SqliteHistoryDatabase.CalendarMetaRow(
                CalKey(ns, id), id, ns, "(deleted)", CalendarConstants.DefaultCalendarColor,
                del.ToString("o"), _auth.IsAuthenticated, DeleteSyncPayload.AckValue(deletedAtTicks),
                del.ToString("o"), IsVisible: false), ct);
            return true;
        }
        if (!string.IsNullOrEmpty(existing.DeletedAt) && DateTime.Parse(existing.DeletedAt).Ticks >= deletedAtTicks)
            return false;
        if (string.IsNullOrEmpty(existing.DeletedAt) && DateTime.Parse(existing.LastUpdated).Ticks > deletedAtTicks)
            return false;
        await _db.UpsertCalendarMetaAsync(existing with
        {
            DeletedAt = del.ToString("o"),
            LastUpdated = del.ToString("o"),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtTicks)
        }, ct);
        return true;
    }

    public async Task ApplyRemoteCalendarMetaAsync(
        string id, string name, string color, bool isVisible, string? description, long lastUpdatedTicks, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var existing = await _db.GetCalendarMetaByIdAsync(ns, id, ct);
        var last = new DateTime(lastUpdatedTicks, DateTimeKind.Utc);
        var fp = SyncFingerprint.ForCalendar(id, name, color, isVisible, description, lastUpdatedTicks);
        if (existing is null)
        {
            await _db.UpsertCalendarMetaAsync(new SqliteHistoryDatabase.CalendarMetaRow(
                CalKey(ns, id), id, ns, name, color, last.ToString("o"),
                _auth.IsAuthenticated, fp, null, description, null, isVisible), ct);
            return;
        }
        await _db.UpsertCalendarMetaAsync(existing with
        {
            Name = name,
            Color = color,
            IsVisible = isVisible,
            Description = description,
            LastUpdated = last.ToString("o"),
            ContentFingerprint = fp,
            DeletedAt = null
        }, ct);
    }

    public async Task<List<CalendarEventIndex>> LoadEventIndexAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetEventMetasByNamespaceAsync(ns, ct);
        var result = new List<CalendarEventIndex>();
        foreach (var m in metas)
        {
            if (!string.IsNullOrEmpty(m.DeletedAt)) continue;
            var start = DateTime.Parse(m.StartUtc).ToUniversalTime();
            var end = DateTime.Parse(m.EndUtc).ToUniversalTime();
            var hasRrule = !string.IsNullOrEmpty(m.RRule);
            if (!hasRrule && (end <= fromUtc || start >= toUtc)) continue;
            if (hasRrule && start >= toUtc) continue;
            result.Add(new CalendarEventIndex(
                m.Id, m.CalendarId, m.Summary, start, end, m.IsAllDay, m.Status,
                DateTime.Parse(m.LastUpdated).ToUniversalTime(), null, m.RRule, m.Location, m.WorkflowId));
        }
        return result;
    }

    public async Task<List<SyncManifestEntry>> LoadEventManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetEventMetasByNamespaceAsync(ns, ct);
        var entries = new List<SyncManifestEntry>();
        foreach (var m in metas)
        {
            long? delTicks = string.IsNullOrEmpty(m.DeletedAt) ? null : DateTime.Parse(m.DeletedAt).Ticks;
            var fp = delTicks.HasValue
                ? DeleteSyncPayload.AckValue(delTicks.Value)
                : m.ContentFingerprint ?? "";
            if (!delTicks.HasValue && string.IsNullOrEmpty(fp) && backfillMissingFingerprints)
            {
                var evt = await LoadEventAsync(m.Id, ct);
                if (evt != null) fp = SyncFingerprint.ForCalendarEvent(evt);
            }
            entries.Add(new SyncManifestEntry(m.Id, m.Summary, DateTime.Parse(m.LastUpdated).Ticks, fp, delTicks));
        }
        return entries;
    }

    public async Task<CalendarEvent?> LoadEventAsync(string eventId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var encrypted = await _db.GetEventContentAsync(EvtContentKey(ns, eventId), ct);
        if (string.IsNullOrEmpty(encrypted))
        {
            var meta = await _db.GetEventMetaByIdAsync(ns, eventId, ct);
            if (meta is null || !string.IsNullOrEmpty(meta.DeletedAt)) return null;
            return new CalendarEvent(
                meta.Id, meta.CalendarId, meta.Summary,
                DateTime.Parse(meta.StartUtc).ToUniversalTime(),
                DateTime.Parse(meta.EndUtc).ToUniversalTime(),
                meta.IsAllDay, null, meta.Location, null, meta.RRule,
                Status: meta.Status,
                ModifiedUtc: DateTime.Parse(meta.LastUpdated).ToUniversalTime(),
                WorkflowId: meta.WorkflowId);
        }

        var keyB64 = await GetEncKeyAsync();
        var json = encrypted;
        if (!string.IsNullOrEmpty(keyB64))
            json = await _crypto.DecryptAsync(keyB64, encrypted, ct);
        if (string.IsNullOrEmpty(json)) return null;
        return JsonSerializer.Deserialize<CalendarEvent>(json, JsonOpts);
    }

    public async Task<List<CalendarEvent>> LoadEventsForCalendarAsync(string calendarId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetEventMetasByNamespaceAsync(ns, ct);
        var result = new List<CalendarEvent>();
        foreach (var m in metas.Where(x =>
                     string.Equals(x.CalendarId, calendarId, StringComparison.OrdinalIgnoreCase)
                     && string.IsNullOrEmpty(x.DeletedAt)))
        {
            var full = await LoadEventAsync(m.Id, ct);
            if (full != null) result.Add(full);
        }
        return result;
    }

    public async Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var now = DateTime.UtcNow;
        var stored = evt with
        {
            CreatedUtc = evt.CreatedUtc ?? now,
            ModifiedUtc = evt.ModifiedUtc ?? now,
            DeletedAt = null
        };
        var fp = SyncFingerprint.ForCalendarEvent(stored);
        var json = JsonSerializer.Serialize(stored, JsonOpts);
        var keyB64 = await GetEncKeyAsync();
        var toStore = json;
        if (!string.IsNullOrEmpty(keyB64))
            toStore = await _crypto.EncryptAsync(keyB64, json, ct);

        await _db.SetEventContentAsync(EvtContentKey(ns, stored.Id), toStore, ct);
        await _db.UpsertEventMetaAsync(new SqliteHistoryDatabase.EventMetaRow(
            EvtMetaKey(ns, stored.Id),
            stored.Id,
            stored.CalendarId,
            ns,
            stored.Summary,
            stored.StartUtc.ToUniversalTime().ToString("o"),
            stored.EndUtc.ToUniversalTime().ToString("o"),
            stored.IsAllDay,
            stored.Status,
            (stored.ModifiedUtc ?? now).ToUniversalTime().ToString("o"),
            fp,
            null,
            stored.RRule,
            stored.Location,
            stored.WorkflowId), ct);
    }

    public Task SoftDeleteEventAsync(string eventId, CancellationToken ct = default) =>
        DeleteEventAsync(eventId, ct);

    public async Task<DateTime> DeleteEventAsync(string eventId, CancellationToken ct = default)
    {
        var deletedAt = DateTime.UtcNow;
        var ns = GetPrefix();
        var existing = await _db.GetEventMetaByIdAsync(ns, eventId, ct);
        if (existing is null) return deletedAt;
        await _db.UpsertEventMetaAsync(existing with
        {
            DeletedAt = deletedAt.ToString("o"),
            LastUpdated = deletedAt.ToString("o"),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAt.Ticks)
        }, ct);
        await _db.DeleteEventContentAsync(EvtContentKey(ns, eventId), ct);
        return deletedAt;
    }

    public async Task<bool> ShouldAcceptIncomingEventAsync(string eventId, CalendarEvent remote, CancellationToken ct = default)
    {
        var local = await LoadEventAsync(eventId, ct);
        if (local is null) return true;
        var merge = CalendarSyncMerger.Merge(local, remote);
        return merge.PreferRemote && !merge.Equal;
    }

    public async Task<bool> TryApplyRemoteEventDeleteAsync(string eventId, long deletedAtTicks, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var existing = await _db.GetEventMetaByIdAsync(ns, eventId, ct);
        var del = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        if (existing is null)
        {
            await _db.UpsertEventMetaAsync(new SqliteHistoryDatabase.EventMetaRow(
                EvtMetaKey(ns, eventId), eventId, "", ns, "(deleted)",
                del.ToString("o"), del.ToString("o"), false, "CANCELLED",
                del.ToString("o"), DeleteSyncPayload.AckValue(deletedAtTicks), del.ToString("o")), ct);
            return true;
        }
        if (!string.IsNullOrEmpty(existing.DeletedAt) && DateTime.Parse(existing.DeletedAt).Ticks >= deletedAtTicks)
            return false;
        if (string.IsNullOrEmpty(existing.DeletedAt) && DateTime.Parse(existing.LastUpdated).Ticks > deletedAtTicks)
            return false;
        await _db.UpsertEventMetaAsync(existing with
        {
            DeletedAt = del.ToString("o"),
            LastUpdated = del.ToString("o"),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtTicks)
        }, ct);
        return true;
    }
}
