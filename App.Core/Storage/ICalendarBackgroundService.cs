namespace App.Core.Storage;

/// <summary>
/// Device-local calendar ticker: sound alerts and ICS subscription refresh.
/// Runs while the app is open (WASM) or the desktop process is in the tray (MAUI).
/// </summary>
public interface ICalendarBackgroundService
{
    /// <summary>Alarms whose repeat window still includes now (not yet expired).</summary>
    Task<IReadOnlyList<CalendarDueAlarm>> GetDueAlarmsAsync(CancellationToken ct = default);
    Task TickSubscriptionsAsync(CancellationToken ct = default);
}
