using App.Core.Auth;
using App.Core.Storage;
using Microsoft.JSInterop;
using System.Text.Json;

namespace App.Client.Services;

/// <summary>
/// Client-side calendar storage (calendarMetas + eventMetas + eventContents).
/// Listing fields stay cleartext; full event JSON is AES-GCM encrypted.
/// </summary>
public class WasmCalendarStore : ICalendarStore
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
    private readonly IJSRuntime _js;

    public WasmCalendarStore(IAuthService auth, ICryptoService crypto, IJSRuntime js)
    {
        _auth = auth;
        _crypto = crypto;
        _js = js;
    }

    private record StoredCalMeta(
        string key,
        string id,
        string @namespace,
        string name,
        string color,
        string lastUpdated,
        bool syncEnabled,
        string? contentFingerprint,
        string? deletedAt,
        string? description = null,
        string? timeZone = null,
        bool? isVisible = null,
        int? sortOrder = null,
        bool? isWorkflowCalendar = null);

    private record StoredEvtMeta(
        string key,
        string id,
        string calendarId,
        string @namespace,
        string summary,
        string startUtc,
        string endUtc,
        bool isAllDay,
        string status,
        string lastUpdated,
        string? contentFingerprint,
        string? deletedAt,
        string? rrule = null,
        string? location = null,
        string? workflowId = null);

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private async Task<string> GetEncKeyAsync() =>
        await _auth.GetOrCreateHistoryEncryptionKeyAsync();

    private string CalKey(string ns, string id) => ns + CalPrefix + id;
    private string EvtMetaKey(string ns, string id) => ns + EvtPrefix + id;
    private string EvtContentKey(string ns, string id) => ns + EvtPrefix + "c-" + id;

    // ── Calendars ──────────────────────────────────────────────────────────

    public async Task<List<LocalCalendar>> LoadCalendarsAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        List<StoredCalMeta>? metas;
        try
        {
            metas = await _js.InvokeAsync<List<StoredCalMeta>?>("idbGetCalendarMetasByNamespace", ns);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmCalendarStore] LoadCalendars failed: {ex.Message}");
            return new List<LocalCalendar>();
        }

        return (metas ?? new List<StoredCalMeta>())
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .OrderBy(m => m.sortOrder ?? 0)
            .ThenBy(m => m.name, StringComparer.OrdinalIgnoreCase)
            .Select(ToLocalCalendar)
            .ToList();
    }

    public async Task EnsureDefaultCalendarAsync(CancellationToken ct = default)
    {
        var list = await LoadCalendarsAsync(ct);
        if (list.Count > 0) return;

        var id = Guid.NewGuid().ToString("N");
        await CreateCalendarAsync(
            id,
            CalendarConstants.DefaultCalendarName,
            CalendarConstants.DefaultCalendarColor,
            description: null,
            ct);
    }

    public async Task CreateCalendarAsync(string id, string name, string color, string? description = null, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var now = DateTime.UtcNow;
        var existing = await LoadCalendarsAsync(ct);
        var sort = existing.Count == 0 ? 0 : existing.Max(c => c.SortOrder) + 1;
        var fp = SyncFingerprint.ForCalendar(id, name, color, isVisible: true, description, now.Ticks);

        var meta = new StoredCalMeta(
            key: CalKey(ns, id),
            id: id,
            @namespace: ns,
            name: name,
            color: color,
            lastUpdated: now.ToString("o"),
            syncEnabled: _auth.IsAuthenticated,
            contentFingerprint: fp,
            deletedAt: null,
            description: description,
            timeZone: null,
            isVisible: true,
            sortOrder: sort,
            isWorkflowCalendar: false);

        await _js.InvokeVoidAsync("idbPutCalendarMeta", meta);
    }

    public async Task UpdateCalendarAsync(string id, string name, string color, bool isVisible, string? description = null, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var existing = await GetCalMetaAsync(id);
        if (existing is null || !string.IsNullOrEmpty(existing.deletedAt)) return;

        var now = DateTime.UtcNow;
        var fp = SyncFingerprint.ForCalendar(id, name, color, isVisible, description, now.Ticks);
        var meta = existing with
        {
            name = name,
            color = color,
            isVisible = isVisible,
            description = description,
            lastUpdated = now.ToString("o"),
            contentFingerprint = fp
        };
        await _js.InvokeVoidAsync("idbPutCalendarMeta", meta);
    }

    public async Task SetCalendarVisibleAsync(string id, bool isVisible, CancellationToken ct = default)
    {
        var existing = await GetCalMetaAsync(id);
        if (existing is null || !string.IsNullOrEmpty(existing.deletedAt)) return;
        await UpdateCalendarAsync(
            id,
            existing.name,
            existing.color,
            isVisible,
            existing.description,
            ct);
    }

    public async Task ReorderCalendarsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var existing = await GetCalMetaAsync(orderedIds[i]);
            if (existing is null || !string.IsNullOrEmpty(existing.deletedAt)) continue;
            var meta = existing with { sortOrder = i };
            await _js.InvokeVoidAsync("idbPutCalendarMeta", meta);
        }
    }

    public async Task<DateTime> DeleteCalendarAsync(string id, CancellationToken ct = default)
    {
        var deletedAt = DateTime.UtcNow;
        var existing = await GetCalMetaAsync(id);
        if (existing is null) return deletedAt;

        var meta = existing with
        {
            deletedAt = deletedAt.ToString("o"),
            lastUpdated = deletedAt.ToString("o"),
            contentFingerprint = DeleteSyncPayload.AckValue(deletedAt.Ticks)
        };
        await _js.InvokeVoidAsync("idbPutCalendarMeta", meta);

        // Soft-delete all events on this calendar
        var events = await LoadEventsForCalendarAsync(id, ct);
        foreach (var e in events)
            await SoftDeleteEventAsync(e.Id, ct);

        return deletedAt;
    }

    public async Task<List<SyncManifestEntry>> LoadCalendarManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredCalMeta>>("idbGetCalendarMetasByNamespace", ns);
        var entries = new List<SyncManifestEntry>();
        foreach (var m in metas)
        {
            long? delTicks = null;
            if (!string.IsNullOrEmpty(m.deletedAt))
                delTicks = DateTime.Parse(m.deletedAt).Ticks;

            var fp = delTicks.HasValue
                ? DeleteSyncPayload.AckValue(delTicks.Value)
                : m.contentFingerprint ?? "";

            if (!delTicks.HasValue && string.IsNullOrEmpty(fp) && backfillMissingFingerprints)
            {
                var last = DateTime.Parse(m.lastUpdated);
                fp = SyncFingerprint.ForCalendar(m.id, m.name, m.color, m.isVisible ?? true, m.description, last.Ticks);
            }

            entries.Add(new SyncManifestEntry(
                m.id,
                m.name,
                DateTime.Parse(m.lastUpdated).Ticks,
                fp,
                delTicks));
        }
        return entries;
    }

    public async Task<bool> ShouldAcceptIncomingCalendarAsync(string id, long remoteLastUpdatedTicks, CancellationToken ct = default)
    {
        var existing = await GetCalMetaAsync(id);
        if (existing is null) return true;
        var local = DateTime.Parse(existing.lastUpdated).Ticks;
        return remoteLastUpdatedTicks > local;
    }

    public async Task<bool> TryApplyRemoteCalendarDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default)
    {
        var existing = await GetCalMetaAsync(id);
        if (existing is null)
        {
            // Tombstone for unknown remote delete
            var ns = GetPrefix();
            var deletedAt = new DateTime(deletedAtTicks, DateTimeKind.Utc);
            var meta = new StoredCalMeta(
                CalKey(ns, id), id, ns, "(deleted)", CalendarConstants.DefaultCalendarColor,
                deletedAt.ToString("o"), _auth.IsAuthenticated,
                DeleteSyncPayload.AckValue(deletedAtTicks), deletedAt.ToString("o"),
                isVisible: false, sortOrder: 0);
            await _js.InvokeVoidAsync("idbPutCalendarMeta", meta);
            return true;
        }

        if (!string.IsNullOrEmpty(existing.deletedAt))
        {
            var localDel = DateTime.Parse(existing.deletedAt).Ticks;
            if (localDel >= deletedAtTicks) return false;
        }
        else
        {
            var localUp = DateTime.Parse(existing.lastUpdated).Ticks;
            if (localUp > deletedAtTicks) return false;
        }

        var del = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        var updated = existing with
        {
            deletedAt = del.ToString("o"),
            lastUpdated = del.ToString("o"),
            contentFingerprint = DeleteSyncPayload.AckValue(deletedAtTicks)
        };
        await _js.InvokeVoidAsync("idbPutCalendarMeta", updated);
        return true;
    }

    public async Task ApplyRemoteCalendarMetaAsync(
        string id,
        string name,
        string color,
        bool isVisible,
        string? description,
        long lastUpdatedTicks,
        CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var existing = await GetCalMetaAsync(id);
        var last = new DateTime(lastUpdatedTicks, DateTimeKind.Utc);
        var fp = SyncFingerprint.ForCalendar(id, name, color, isVisible, description, lastUpdatedTicks);

        if (existing is null)
        {
            var meta = new StoredCalMeta(
                CalKey(ns, id), id, ns, name, color, last.ToString("o"),
                _auth.IsAuthenticated, fp, null, description, null, isVisible, 0, false);
            await _js.InvokeVoidAsync("idbPutCalendarMeta", meta);
            return;
        }

        var updated = existing with
        {
            name = name,
            color = color,
            isVisible = isVisible,
            description = description,
            lastUpdated = last.ToString("o"),
            contentFingerprint = fp,
            deletedAt = null
        };
        await _js.InvokeVoidAsync("idbPutCalendarMeta", updated);
    }

    // ── Events ─────────────────────────────────────────────────────────────

    public async Task<List<CalendarEventIndex>> LoadEventIndexAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        List<StoredEvtMeta>? metas;
        try
        {
            metas = await _js.InvokeAsync<List<StoredEvtMeta>?>("idbGetEventMetasByNamespace", ns);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmCalendarStore] LoadEventIndex failed: {ex.Message}");
            return new List<CalendarEventIndex>();
        }

        var result = new List<CalendarEventIndex>();
        foreach (var m in metas ?? new List<StoredEvtMeta>())
        {
            if (!string.IsNullOrEmpty(m.deletedAt)) continue;
            var start = DateTime.Parse(m.startUtc).ToUniversalTime();
            var end = DateTime.Parse(m.endUtc).ToUniversalTime();
            // Include if range overlaps [from, to). Recurring masters may start before window.
            var hasRrule = !string.IsNullOrEmpty(m.rrule);
            if (!hasRrule && (end <= fromUtc || start >= toUtc)) continue;
            if (hasRrule && start >= toUtc) continue;

            result.Add(new CalendarEventIndex(
                m.id,
                m.calendarId,
                m.summary,
                start,
                end,
                m.isAllDay,
                m.status ?? "CONFIRMED",
                DateTime.Parse(m.lastUpdated).ToUniversalTime(),
                null,
                m.rrule,
                m.location,
                m.workflowId));
        }
        return result;
    }

    public async Task<List<SyncManifestEntry>> LoadEventManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredEvtMeta>>("idbGetEventMetasByNamespace", ns);
        var entries = new List<SyncManifestEntry>();
        foreach (var m in metas)
        {
            long? delTicks = null;
            if (!string.IsNullOrEmpty(m.deletedAt))
                delTicks = DateTime.Parse(m.deletedAt).Ticks;

            var fp = delTicks.HasValue
                ? DeleteSyncPayload.AckValue(delTicks.Value)
                : m.contentFingerprint ?? "";

            if (!delTicks.HasValue && string.IsNullOrEmpty(fp) && backfillMissingFingerprints)
            {
                var evt = await LoadEventAsync(m.id, ct);
                if (evt != null)
                    fp = SyncFingerprint.ForCalendarEvent(evt);
            }

            entries.Add(new SyncManifestEntry(
                m.id,
                m.summary,
                DateTime.Parse(m.lastUpdated).Ticks,
                fp,
                delTicks));
        }
        return entries;
    }

    public async Task<CalendarEvent?> LoadEventAsync(string eventId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var contentKey = EvtContentKey(ns, eventId);
        var encrypted = await _js.InvokeAsync<string?>("idbGetEventContent", contentKey);
        if (string.IsNullOrEmpty(encrypted))
        {
            // Fall back to meta-only reconstruction
            var meta = await GetEvtMetaAsync(eventId);
            if (meta is null || !string.IsNullOrEmpty(meta.deletedAt)) return null;
            return FromMetaOnly(meta);
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
        var metas = await _js.InvokeAsync<List<StoredEvtMeta>>("idbGetEventMetasByNamespace", ns);
        var result = new List<CalendarEvent>();
        foreach (var m in metas.Where(x => string.Equals(x.calendarId, calendarId, StringComparison.OrdinalIgnoreCase)
                                           && string.IsNullOrEmpty(x.deletedAt)))
        {
            var full = await LoadEventAsync(m.id, ct);
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

        await _js.InvokeVoidAsync("idbPutEventContent", EvtContentKey(ns, stored.Id), toStore);

        var meta = new StoredEvtMeta(
            key: EvtMetaKey(ns, stored.Id),
            id: stored.Id,
            calendarId: stored.CalendarId,
            @namespace: ns,
            summary: stored.Summary,
            startUtc: stored.StartUtc.ToUniversalTime().ToString("o"),
            endUtc: stored.EndUtc.ToUniversalTime().ToString("o"),
            isAllDay: stored.IsAllDay,
            status: stored.Status,
            lastUpdated: (stored.ModifiedUtc ?? now).ToUniversalTime().ToString("o"),
            contentFingerprint: fp,
            deletedAt: null,
            rrule: stored.RRule,
            location: stored.Location,
            workflowId: stored.WorkflowId);

        await _js.InvokeVoidAsync("idbPutEventMeta", meta);
    }

    public async Task SoftDeleteEventAsync(string eventId, CancellationToken ct = default)
    {
        await DeleteEventAsync(eventId, ct);
    }

    public async Task<DateTime> DeleteEventAsync(string eventId, CancellationToken ct = default)
    {
        var deletedAt = DateTime.UtcNow;
        var ns = GetPrefix();
        var existing = await GetEvtMetaAsync(eventId);
        if (existing is null) return deletedAt;

        var meta = existing with
        {
            deletedAt = deletedAt.ToString("o"),
            lastUpdated = deletedAt.ToString("o"),
            contentFingerprint = DeleteSyncPayload.AckValue(deletedAt.Ticks)
        };
        await _js.InvokeVoidAsync("idbPutEventMeta", meta);
        await _js.InvokeVoidAsync("idbDeleteEventContent", EvtContentKey(ns, eventId));
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
        var existing = await GetEvtMetaAsync(eventId);
        if (existing is null)
        {
            var ns = GetPrefix();
            var del = new DateTime(deletedAtTicks, DateTimeKind.Utc);
            var meta = new StoredEvtMeta(
                EvtMetaKey(ns, eventId), eventId, "", ns, "(deleted)",
                del.ToString("o"), del.ToString("o"), false, "CANCELLED",
                del.ToString("o"), DeleteSyncPayload.AckValue(deletedAtTicks), del.ToString("o"));
            await _js.InvokeVoidAsync("idbPutEventMeta", meta);
            return true;
        }

        if (!string.IsNullOrEmpty(existing.deletedAt))
        {
            if (DateTime.Parse(existing.deletedAt).Ticks >= deletedAtTicks) return false;
        }
        else if (DateTime.Parse(existing.lastUpdated).Ticks > deletedAtTicks)
        {
            return false;
        }

        var deletedAt = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        var updated = existing with
        {
            deletedAt = deletedAt.ToString("o"),
            lastUpdated = deletedAt.ToString("o"),
            contentFingerprint = DeleteSyncPayload.AckValue(deletedAtTicks)
        };
        await _js.InvokeVoidAsync("idbPutEventMeta", updated);
        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<StoredCalMeta?> GetCalMetaAsync(string id)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredCalMeta>>("idbGetCalendarMetasByNamespace", ns);
        return metas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<StoredEvtMeta?> GetEvtMetaAsync(string id)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredEvtMeta>>("idbGetEventMetasByNamespace", ns);
        return metas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static LocalCalendar ToLocalCalendar(StoredCalMeta m) => new(
        m.id,
        string.IsNullOrWhiteSpace(m.name) ? "(empty)" : m.name,
        string.IsNullOrWhiteSpace(m.color) ? CalendarConstants.DefaultCalendarColor : m.color,
        DateTime.Parse(m.lastUpdated),
        m.description,
        m.timeZone,
        m.isVisible ?? true,
        m.sortOrder ?? 0,
        m.isWorkflowCalendar ?? false);

    private static CalendarEvent FromMetaOnly(StoredEvtMeta m) => new(
        m.id,
        m.calendarId,
        m.summary,
        DateTime.Parse(m.startUtc).ToUniversalTime(),
        DateTime.Parse(m.endUtc).ToUniversalTime(),
        m.isAllDay,
        Description: null,
        Location: m.location,
        RRule: m.rrule,
        Status: m.status ?? "CONFIRMED",
        ModifiedUtc: DateTime.Parse(m.lastUpdated).ToUniversalTime(),
        DeletedAt: string.IsNullOrEmpty(m.deletedAt) ? null : DateTime.Parse(m.deletedAt).ToUniversalTime(),
        WorkflowId: m.workflowId);
}
