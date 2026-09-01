using App.Core.Storage;

namespace App.Shared.Services;

public sealed class CalendarBackgroundService : ICalendarBackgroundService
{
    private readonly ICalendarStore _store;
    private readonly CalendarSubscriptionService _subscriptions;

    public CalendarBackgroundService(
        ICalendarStore store,
        CalendarSubscriptionService subscriptions)
    {
        _store = store;
        _subscriptions = subscriptions;
    }

    public async Task<IReadOnlyList<CalendarDueAlarm>> GetDueAlarmsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var from = now.AddDays(-1);
        var to = now.AddHours(36);
        List<CalendarEventIndex> index;
        Dictionary<string, string> colors;
        try
        {
            var cals = await _store.LoadCalendarsAsync(ct);
            colors = cals.ToDictionary(c => c.Id, c => c.Color, StringComparer.OrdinalIgnoreCase);
            index = await _store.LoadEventIndexAsync(from, to, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CalendarRemind] load failed: {ex.Message}");
            return [];
        }

        var withAlerts = index.Where(e => e.ReminderMinutesBefore is not null).ToList();
        if (withAlerts.Count == 0)
            return [];

        var occs = CalendarIcs.ExpandOccurrences(withAlerts, colors, from, to);
        var due = new List<CalendarDueAlarm>();
        foreach (var occ in occs)
        {
            ct.ThrowIfCancellationRequested();
            var src = withAlerts.FirstOrDefault(e =>
                string.Equals(e.Id, occ.EventId, StringComparison.OrdinalIgnoreCase));
            if (src?.ReminderMinutesBefore is not { } minutes)
                continue;

            var trigger = CalendarReminder.TriggerUtc(occ, minutes);
            var repeat = CalendarReminder.NormalizeRepeatMinutes(src.ReminderRepeatMinutes);
            var until = trigger.AddMinutes(repeat);
            if (now < trigger || now >= until)
                continue;

            due.Add(new CalendarDueAlarm(
                occ.EventId,
                occ.Summary,
                occ.StartUtc,
                trigger,
                until,
                CalendarReminder.NormalizeSoundId(src.ReminderSoundId),
                repeat));
        }

        return due
            .OrderBy(a => a.TriggerUtc)
            .ToList();
    }

    public Task TickSubscriptionsAsync(CancellationToken ct = default) =>
        _subscriptions.RefreshDueAsync(ct);
}
