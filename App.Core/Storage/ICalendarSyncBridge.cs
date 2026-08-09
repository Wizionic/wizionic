namespace App.Core.Storage;

/// <summary>
/// Hooks for calendar/event save/delete to trigger cross-device sync, and UI refresh on remote changes.
/// </summary>
public interface ICalendarSyncBridge
{
    event Action? OnCalendarsChanged;

    void ScheduleAutoSyncCalendarAfterLocalSave(string calendarId);
    void ScheduleAutoSyncCalendarDeleteAfterLocalDelete(string calendarId, DateTime deletedAt);

    void ScheduleAutoSyncEventAfterLocalSave(string calendarId, string eventId);
    void ScheduleAutoSyncEventDeleteAfterLocalDelete(string calendarId, string eventId, DateTime deletedAt);
}
