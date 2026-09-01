namespace App.Core.Storage;

/// <summary>
/// Local-first calendar persistence (WASM IndexedDB / MAUI SQLite).
/// Calendar meta and event listing fields are cleartext; full event payloads may be encrypted.
/// </summary>
public interface ICalendarStore
{
    // ── Calendars ──────────────────────────────────────────────────────────

    Task<List<LocalCalendar>> LoadCalendarsAsync(CancellationToken ct = default);
    Task<List<SyncManifestEntry>> LoadCalendarManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default);

    Task EnsureDefaultCalendarAsync(CancellationToken ct = default);

    Task CreateCalendarAsync(string id, string name, string color, string? description = null, CancellationToken ct = default, bool isWorkflowCalendar = false);

    /// <summary>Ensures a system "Workflows" calendar exists; returns its id.</summary>
    Task<string> EnsureWorkflowCalendarAsync(CancellationToken ct = default);
    Task UpdateCalendarAsync(string id, string name, string color, bool isVisible, string? description = null, CancellationToken ct = default);
    Task SetCalendarVisibleAsync(string id, bool isVisible, CancellationToken ct = default);
    Task ReorderCalendarsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default);
    Task<DateTime> DeleteCalendarAsync(string id, CancellationToken ct = default);

    Task<bool> ShouldAcceptIncomingCalendarAsync(string id, long remoteLastUpdatedTicks, CancellationToken ct = default);
    Task<bool> TryApplyRemoteCalendarDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default);
    Task ApplyRemoteCalendarMetaAsync(
        string id,
        string name,
        string color,
        bool isVisible,
        string? description,
        long lastUpdatedTicks,
        CancellationToken ct = default,
        string? subscriptionUrl = null,
        int? refreshIntervalMinutes = null);

    /// <summary>Create or update a subscribed ICS calendar's URL and fetch cache.</summary>
    Task SetCalendarSubscriptionAsync(
        string id,
        string? url,
        int? refreshIntervalMinutes,
        string? etag,
        string? lastModified,
        DateTime? lastFetchedUtc,
        CancellationToken ct = default);

    // ── Events ─────────────────────────────────────────────────────────────

    /// <summary>Index rows overlapping [fromUtc, toUtc) for visible grids (excludes soft-deleted).</summary>
    Task<List<CalendarEventIndex>> LoadEventIndexAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    Task<List<SyncManifestEntry>> LoadEventManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default);

    Task<CalendarEvent?> LoadEventAsync(string eventId, CancellationToken ct = default);
    Task<List<CalendarEvent>> LoadEventsForCalendarAsync(string calendarId, CancellationToken ct = default);

    Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default);
    Task SoftDeleteEventAsync(string eventId, CancellationToken ct = default);
    Task<DateTime> DeleteEventAsync(string eventId, CancellationToken ct = default);

    Task<bool> ShouldAcceptIncomingEventAsync(string eventId, CalendarEvent remote, CancellationToken ct = default);
    Task<bool> TryApplyRemoteEventDeleteAsync(string eventId, long deletedAtTicks, CancellationToken ct = default);
}
