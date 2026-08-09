using App.Core.Storage;

namespace App.Shared.Services;

/// <summary>No-op calendar store for host static render of shared layout/components.</summary>
public sealed class NullCalendarStore : ICalendarStore
{
    public static readonly NullCalendarStore Instance = new();

    public Task<List<LocalCalendar>> LoadCalendarsAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<LocalCalendar>());

    public Task<List<SyncManifestEntry>> LoadCalendarManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default) =>
        Task.FromResult(new List<SyncManifestEntry>());

    public Task EnsureDefaultCalendarAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task CreateCalendarAsync(string id, string name, string color, string? description = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateCalendarAsync(string id, string name, string color, bool isVisible, string? description = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SetCalendarVisibleAsync(string id, bool isVisible, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ReorderCalendarsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<DateTime> DeleteCalendarAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(DateTime.UtcNow);

    public Task<bool> ShouldAcceptIncomingCalendarAsync(string id, long remoteLastUpdatedTicks, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> TryApplyRemoteCalendarDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task ApplyRemoteCalendarMetaAsync(
        string id, string name, string color, bool isVisible, string? description, long lastUpdatedTicks, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<CalendarEventIndex>> LoadEventIndexAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        Task.FromResult(new List<CalendarEventIndex>());

    public Task<List<SyncManifestEntry>> LoadEventManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default) =>
        Task.FromResult(new List<SyncManifestEntry>());

    public Task<CalendarEvent?> LoadEventAsync(string eventId, CancellationToken ct = default) =>
        Task.FromResult<CalendarEvent?>(null);

    public Task<List<CalendarEvent>> LoadEventsForCalendarAsync(string calendarId, CancellationToken ct = default) =>
        Task.FromResult(new List<CalendarEvent>());

    public Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default) => Task.CompletedTask;

    public Task SoftDeleteEventAsync(string eventId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<DateTime> DeleteEventAsync(string eventId, CancellationToken ct = default) =>
        Task.FromResult(DateTime.UtcNow);

    public Task<bool> ShouldAcceptIncomingEventAsync(string eventId, CalendarEvent remote, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> TryApplyRemoteEventDeleteAsync(string eventId, long deletedAtTicks, CancellationToken ct = default) =>
        Task.FromResult(false);
}
