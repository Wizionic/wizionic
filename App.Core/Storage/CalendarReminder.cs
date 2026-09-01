namespace App.Core.Storage;

public static class CalendarReminder
{
    public const string ChannelSound = "sound";
    public const string DefaultSoundId = "chime";
    public const int DefaultRepeatMinutes = 1;
    public const int SoundGapMs = 500;

    public static readonly (int Minutes, string Label)[] RepeatDurations =
    [
        (1, "1 minute"),
        (5, "5 minutes")
    ];

    public static readonly (string Id, string Label)[] Sounds =
    [
        ("chime", "Chime"),
        ("ping", "Ping"),
        ("bell", "Bell"),
        ("glass", "Glass"),
        ("wood", "Wood"),
        ("soft", "Soft")
    ];

    public static readonly (int? Minutes, string Label)[] Offsets =
    [
        (null, "None"),
        (0, "At time of event"),
        (5, "5 minutes before"),
        (10, "10 minutes before"),
        (15, "15 minutes before"),
        (30, "30 minutes before"),
        (60, "1 hour before"),
        (1440, "1 day before")
    ];

    public static string NormalizeSoundId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return DefaultSoundId;
        var t = id.Trim().ToLowerInvariant();
        return Sounds.Any(s => s.Id == t) ? t : DefaultSoundId;
    }

    public static int? NormalizeMinutes(int? minutes)
    {
        if (minutes is null)
            return null;
        foreach (var (m, _) in Offsets)
        {
            if (m == minutes)
                return m;
        }
        // Snap imported VALARM values to the nearest supported offset.
        var v = minutes.Value;
        if (v < 0) return 0;
        int? best = 0;
        var bestDist = int.MaxValue;
        foreach (var (m, _) in Offsets)
        {
            if (m is null) continue;
            var d = Math.Abs(m.Value - v);
            if (d < bestDist)
            {
                bestDist = d;
                best = m;
            }
        }
        return best;
    }

    /// <summary>
    /// UTC instant the alert should fire for one occurrence.
    /// All-day events use 09:00 local on the occurrence date.
    /// </summary>
    public static DateTime TriggerUtc(CalendarOccurrence occ, int minutesBefore)
    {
        DateTime startUtc;
        if (occ.IsAllDay)
        {
            var localDate = occ.StartUtc.Kind == DateTimeKind.Utc
                ? occ.StartUtc.ToLocalTime().Date
                : DateTime.SpecifyKind(occ.StartUtc, DateTimeKind.Local).Date;
            startUtc = DateTime.SpecifyKind(localDate.AddHours(9), DateTimeKind.Local).ToUniversalTime();
        }
        else
        {
            startUtc = occ.StartUtc.Kind == DateTimeKind.Utc
                ? occ.StartUtc
                : occ.StartUtc.ToUniversalTime();
        }

        return startUtc.AddMinutes(-Math.Max(0, minutesBefore));
    }

    public static int NormalizeRepeatMinutes(int? minutes) =>
        minutes is 5 ? 5 : DefaultRepeatMinutes;

    public static CalendarEvent CopyLocalReminders(CalendarEvent incoming, CalendarEvent? previous)
    {
        if (previous is null)
            return incoming;
        if (incoming.ReminderMinutesBefore is not null)
            return incoming with
            {
                ReminderRepeatMinutes = incoming.ReminderRepeatMinutes ?? previous.ReminderRepeatMinutes
            };
        return incoming with
        {
            ReminderMinutesBefore = previous.ReminderMinutesBefore,
            ReminderSoundId = previous.ReminderSoundId,
            ReminderChannel = previous.ReminderChannel,
            ReminderRepeatMinutes = previous.ReminderRepeatMinutes
        };
    }
}

public record CalendarDueAlarm(
    string EventId,
    string Summary,
    DateTime OccurrenceStartUtc,
    DateTime TriggerUtc,
    DateTime UntilUtc,
    string SoundId,
    int RepeatMinutes);
