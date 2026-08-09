using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace App.Core.Storage;

/// <summary>
/// RFC 5545 import/export and recurrence expansion via Ical.Net 5.
/// Domain events use UTC storage; all-day uses date-only CalDateTime.
/// </summary>
public static class CalendarIcs
{
    public sealed record ImportResult(
        string? CalendarName,
        IReadOnlyList<CalendarEvent> Events,
        int Skipped);

    /// <summary>Export one calendar's events to a .ics string.</summary>
    public static string ExportCalendar(LocalCalendar calendar, IEnumerable<CalendarEvent> events)
    {
        var ical = new Calendar { ProductId = "-//Wizionic//Calendar//EN" };
        ical.Properties.Set("X-WR-CALNAME", calendar.Name);
        if (!string.IsNullOrWhiteSpace(calendar.Description))
            ical.Properties.Set("X-WR-CALDESC", calendar.Description);
        if (!string.IsNullOrWhiteSpace(calendar.Color))
            ical.Properties.Set("X-APPLE-CALENDAR-COLOR", calendar.Color);
        if (!string.IsNullOrWhiteSpace(calendar.TimeZone))
            ical.Properties.Set("X-WR-TIMEZONE", calendar.TimeZone);

        foreach (var e in events.Where(x => x.DeletedAt is null))
            ical.Events.Add(ToIcalEvent(e));

        return new CalendarSerializer().SerializeToString(ical) ?? string.Empty;
    }

    /// <summary>Export a single event as a VCALENDAR with one VEVENT.</summary>
    public static string ExportEvent(LocalCalendar calendar, CalendarEvent evt)
        => ExportCalendar(calendar, [evt]);

    /// <summary>
    /// Parse .ics text into domain events assigned to <paramref name="targetCalendarId"/>.
    /// Preserves UID when present.
    /// </summary>
    public static ImportResult Import(string icsText, string targetCalendarId)
    {
        if (string.IsNullOrWhiteSpace(icsText))
            return new ImportResult(null, Array.Empty<CalendarEvent>(), 0);

        Calendar? cal;
        try
        {
            cal = Calendar.Load(icsText);
        }
        catch
        {
            return new ImportResult(null, Array.Empty<CalendarEvent>(), 0);
        }

        if (cal is null)
            return new ImportResult(null, Array.Empty<CalendarEvent>(), 0);

        var calName = cal.Properties.Get<string>("X-WR-CALNAME")
                      ?? cal.Properties.Get<string>("NAME");
        var events = new List<CalendarEvent>();
        var skipped = 0;

        foreach (var icalEvent in cal.Events)
        {
            try
            {
                var mapped = FromIcalEvent(icalEvent, targetCalendarId);
                if (mapped is null) { skipped++; continue; }
                events.Add(mapped);
            }
            catch
            {
                skipped++;
            }
        }

        // Also merge any additional calendars from a collection load if present
        try
        {
            var collection = CalendarCollection.Load(icsText);
            if (collection is { Count: > 1 })
            {
                for (var i = 1; i < collection.Count; i++)
                {
                    var extra = collection[i];
                    calName ??= extra.Properties.Get<string>("X-WR-CALNAME");
                    foreach (var icalEvent in extra.Events)
                    {
                        try
                        {
                            var mapped = FromIcalEvent(icalEvent, targetCalendarId);
                            if (mapped is null) { skipped++; continue; }
                            // Dedupe by UID
                            if (events.Any(e => string.Equals(e.Id, mapped.Id, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            events.Add(mapped);
                        }
                        catch { skipped++; }
                    }
                }
            }
        }
        catch { /* single-calendar ICS is fine */ }

        return new ImportResult(calName, events, skipped);
    }

    /// <summary>
    /// Expand domain events (including RRULE masters) into occurrences overlapping
    /// <paramref name="rangeStartUtc"/>..<paramref name="rangeEndUtc"/> (half-open).
    /// </summary>
    public static IReadOnlyList<CalendarOccurrence> ExpandOccurrences(
        IEnumerable<CalendarEventIndex> events,
        IReadOnlyDictionary<string, string> colorByCalendarId,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc)
    {
        var result = new List<CalendarOccurrence>();
        var defaultColor = CalendarConstants.DefaultCalendarColor;
        // Safety cap per series so a pathological RRULE cannot freeze the UI
        const int maxPerSeries = 400;

        foreach (var e in events)
        {
            if (e.DeletedAt is not null) continue;
            if (string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;

            var color = colorByCalendarId.TryGetValue(e.CalendarId, out var c) ? c : defaultColor;

            if (string.IsNullOrWhiteSpace(e.RRule))
            {
                if (e.EndUtc > rangeStartUtc && e.StartUtc < rangeEndUtc)
                {
                    result.Add(new CalendarOccurrence(
                        e.Id, e.CalendarId, e.Summary, e.StartUtc, e.EndUtc, e.IsAllDay, color,
                        e.Location, IsRecurring: false));
                }
                continue;
            }

            try
            {
                var icalEvt = ToIcalEventForExpand(e);
                var duration = e.EndUtc - e.StartUtc;
                if (duration <= TimeSpan.Zero)
                    duration = e.IsAllDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);

                // Start enumeration a bit before the window so multi-day instances that began earlier are included
                var searchStart = new CalDateTime(
                    DateTime.SpecifyKind(rangeStartUtc.AddDays(-2).ToUniversalTime(), DateTimeKind.Utc), "UTC");
                var searchEnd = new CalDateTime(
                    DateTime.SpecifyKind(rangeEndUtc.AddDays(1).ToUniversalTime(), DateTimeKind.Utc), "UTC");

                var count = 0;
                foreach (var occ in icalEvt.GetOccurrences(searchStart))
                {
                    var st = occ.Period?.StartTime;
                    if (st is null) continue;
                    if (st.GreaterThanOrEqual(searchEnd)) break;

                    var occStart = FromCalDateTime(st, e.IsAllDay);
                    var occEnd = occStart + duration;

                    if (occEnd > rangeStartUtc && occStart < rangeEndUtc)
                    {
                        result.Add(new CalendarOccurrence(
                            e.Id,
                            e.CalendarId,
                            e.Summary,
                            occStart,
                            occEnd,
                            e.IsAllDay,
                            color,
                            e.Location,
                            IsRecurring: true,
                            RecurrenceId: occStart));
                    }

                    if (++count >= maxPerSeries) break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CalendarIcs] RRULE expand failed for {e.Id}: {ex.Message}");
                if (e.EndUtc > rangeStartUtc && e.StartUtc < rangeEndUtc)
                {
                    result.Add(new CalendarOccurrence(
                        e.Id, e.CalendarId, e.Summary, e.StartUtc, e.EndUtc, e.IsAllDay, color,
                        e.Location, IsRecurring: true));
                }
            }
        }

        return result;
    }

    /// <summary>Parse common repeat presets into RRULE body (no RRULE: prefix).</summary>
    public static string? BuildRRule(string? preset, DateTime startLocal)
    {
        if (string.IsNullOrWhiteSpace(preset) || preset.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;

        return preset.ToLowerInvariant() switch
        {
            "daily" => "FREQ=DAILY",
            "weekly" => $"FREQ=WEEKLY;BYDAY={ToByDay(startLocal.DayOfWeek)}",
            "biweekly" => $"FREQ=WEEKLY;INTERVAL=2;BYDAY={ToByDay(startLocal.DayOfWeek)}",
            // Google-style: "Monthly on the 2nd Saturday" → FREQ=MONTHLY;BYDAY=2SA
            "monthly" => $"FREQ=MONTHLY;BYDAY={NthWeekdayToken(startLocal)}",
            "yearly" => $"FREQ=YEARLY;BYMONTH={startLocal.Month};BYMONTHDAY={startLocal.Day}",
            "weekdays" => "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR",
            _ when preset.StartsWith("FREQ=", StringComparison.OrdinalIgnoreCase) => preset.Trim(),
            _ => null
        };
    }

    /// <summary>
    /// Dropdown label for monthly, e.g. "Monthly on the 2nd Saturday".
    /// </summary>
    public static string MonthlyRepeatOptionLabel(DateTime startLocal)
    {
        var (ordinal, weekday) = NthWeekdayOfMonth(startLocal);
        var ordWord = ordinal switch
        {
            1 => "1st",
            2 => "2nd",
            3 => "3rd",
            4 => "4th",
            _ => "last" // -1 last weekday of month
        };
        var dayName = weekday.ToString(); // Sunday, Monday, ...
        return $"Monthly on the {ordWord} {dayName}";
    }

    /// <summary>
    /// Weekday occurrence within the month (1–4, or -1 for last when it is the final that weekday).
    /// Matches Google Calendar monthly-by-weekday behavior.
    /// </summary>
    public static (int Ordinal, DayOfWeek Weekday) NthWeekdayOfMonth(DateTime date)
    {
        var weekday = date.DayOfWeek;
        var occurrence = ((date.Day - 1) / 7) + 1; // 1st, 2nd, 3rd, 4th, or 5th

        // If another of the same weekday would fall after this date in the month, this is not last.
        // If not, this is the last occurrence → use -1 (RFC 5545 last).
        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        var isLast = date.Day + 7 > daysInMonth;
        if (isLast && occurrence >= 4)
            return (-1, weekday);

        // Cap normal ordinals at 4 (5th only appears when not treated as last above)
        if (occurrence > 4)
            return (-1, weekday);

        return (occurrence, weekday);
    }

    /// <summary>RFC 5545 BYDAY token with ordinal, e.g. 2SA or -1FR.</summary>
    public static string NthWeekdayToken(DateTime date)
    {
        var (ordinal, weekday) = NthWeekdayOfMonth(date);
        return $"{ordinal}{ToByDay(weekday)}";
    }

    public static string RRulePresetLabel(string? rrule, DateTime? startLocal = null)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return "Does not repeat";
        var u = rrule.ToUpperInvariant();
        if (u.Contains("FREQ=DAILY") && !u.Contains("INTERVAL=")) return "Daily";
        if (u.Contains("FREQ=WEEKLY") && u.Contains("BYDAY=MO,TU,WE,TH,FR") && !u.Contains("INTERVAL="))
            return "Every Weekday";
        if (u.Contains("FREQ=MONTHLY") && u.Contains("BYDAY="))
            return startLocal.HasValue ? MonthlyRepeatOptionLabel(startLocal.Value) : "Monthly";
        if (u.Contains("FREQ=MONTHLY")) return "Monthly";
        if (u.Contains("FREQ=YEARLY")) return "Annually";
        if (u.Contains("FREQ=WEEKLY") && u.Contains("INTERVAL=2")) return "Every 2 weeks";
        if (u.Contains("FREQ=WEEKLY")) return "Weekly";
        return "Custom";
    }

    /// <summary>Safe filename stem from calendar name.</summary>
    public static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "calendar";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ' ')
                sb.Append(ch == ' ' ? '-' : ch);
        }
        var s = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(s) ? "calendar" : s;
    }

    // ── Mapping ────────────────────────────────────────────────────────────

    private static IcalEvent ToIcalEvent(CalendarEvent e)
    {
        var evt = new IcalEvent
        {
            Uid = e.Id,
            Summary = e.Summary,
            Description = e.Description,
            Location = e.Location,
            Sequence = e.Sequence,
            DtStart = ToCalDateTime(e.StartUtc, e.IsAllDay),
            DtEnd = ToCalDateTime(e.EndUtc, e.IsAllDay),
            Status = string.IsNullOrWhiteSpace(e.Status) ? "CONFIRMED" : e.Status.ToUpperInvariant(),
            Transparency = string.Equals(e.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase)
                ? "TRANSPARENT"
                : "OPAQUE",
        };

        if (e.CreatedUtc.HasValue)
            evt.Created = new CalDateTime(DateTime.SpecifyKind(e.CreatedUtc.Value.ToUniversalTime(), DateTimeKind.Utc), "UTC");
        if (e.ModifiedUtc.HasValue)
            evt.LastModified = new CalDateTime(DateTime.SpecifyKind(e.ModifiedUtc.Value.ToUniversalTime(), DateTimeKind.Utc), "UTC");

        if (!string.IsNullOrWhiteSpace(e.RRule))
        {
            try { evt.RecurrenceRule = new RecurrenceRule(e.RRule); }
            catch { /* skip bad rule on export */ }
        }

        if (e.ExDates is { Count: > 0 })
        {
            foreach (var ex in e.ExDates)
                evt.ExceptionDates.Add(ToCalDateTime(ex, e.IsAllDay));
        }

        if (e.RDates is { Count: > 0 })
        {
            foreach (var rd in e.RDates)
                evt.RecurrenceDates.Add(ToCalDateTime(rd, e.IsAllDay));
        }

        if (!string.IsNullOrWhiteSpace(e.WorkflowId))
            evt.Properties.Set("X-WIZIONIC-WORKFLOW", e.WorkflowId);

        return evt;
    }

    private static IcalEvent ToIcalEventForExpand(CalendarEventIndex e)
    {
        var evt = new IcalEvent
        {
            Uid = e.Id,
            Summary = e.Summary,
            Location = e.Location,
            DtStart = ToCalDateTime(e.StartUtc, e.IsAllDay),
            DtEnd = ToCalDateTime(e.EndUtc, e.IsAllDay),
        };
        if (!string.IsNullOrWhiteSpace(e.RRule))
            evt.RecurrenceRule = new RecurrenceRule(e.RRule);
        return evt;
    }

    private static CalendarEvent? FromIcalEvent(IcalEvent ical, string calendarId)
    {
        if (ical.DtStart is null) return null;

        var isAllDay = ical.IsAllDay || !ical.DtStart.HasTime;
        var startUtc = FromCalDateTime(ical.DtStart, isAllDay);
        DateTime endUtc;
        if (ical.DtEnd is not null)
            endUtc = FromCalDateTime(ical.DtEnd, isAllDay);
        else if (ical.Duration is { } dur)
            endUtc = startUtc + dur.ToTimeSpan(ical.DtStart);
        else
            endUtc = isAllDay ? startUtc.AddDays(1) : startUtc.AddHours(1);

        string? rrule = null;
        if (ical.RecurrenceRule is not null)
        {
            rrule = ical.RecurrenceRule.ToString()?.Trim();
            if (rrule is not null && rrule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
                rrule = rrule["RRULE:".Length..].Trim();
        }
#pragma warning disable CS0618 // RecurrenceRules obsolete but may be present on imported files
        else if (ical.RecurrenceRules is { Count: > 0 })
        {
            rrule = ical.RecurrenceRules[0].ToString()?.Trim();
            if (rrule is not null && rrule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
                rrule = rrule["RRULE:".Length..].Trim();
        }
#pragma warning restore CS0618

        // ExceptionDates / RecurrenceDates: Add API only — re-export handles them; skip full import lists for v1
        // (EXDATE is applied by Ical when expanding from raw ICS; stored domain events keep RRULE text.)

        var uid = string.IsNullOrWhiteSpace(ical.Uid) ? Guid.NewGuid().ToString("N") : ical.Uid;
        uid = uid.Replace('@', '_').Trim();

        var workflowId = ical.Properties.Get<string>("X-WIZIONIC-WORKFLOW");
        var status = string.IsNullOrWhiteSpace(ical.Status) ? "CONFIRMED" : ical.Status;
        var transp = string.Equals(ical.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase)
            ? "TRANSPARENT"
            : "OPAQUE";

        return new CalendarEvent(
            Id: uid,
            CalendarId: calendarId,
            Summary: string.IsNullOrWhiteSpace(ical.Summary) ? "(no title)" : ical.Summary,
            StartUtc: startUtc,
            EndUtc: endUtc,
            IsAllDay: isAllDay,
            Description: ical.Description,
            Location: ical.Location,
            TimeZoneId: ical.DtStart.TzId,
            RRule: rrule,
            Status: status,
            Transparency: transp,
            Sequence: ical.Sequence,
            CreatedUtc: ical.Created is not null ? FromCalDateTime(ical.Created, false) : DateTime.UtcNow,
            ModifiedUtc: ical.LastModified is not null ? FromCalDateTime(ical.LastModified, false) : DateTime.UtcNow,
            WorkflowId: string.IsNullOrWhiteSpace(workflowId) ? null : workflowId);
    }

    private static CalDateTime ToCalDateTime(DateTime utc, bool isAllDay)
    {
        if (isAllDay)
        {
            var local = utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Local);
            return new CalDateTime(local.Year, local.Month, local.Day);
        }

        var u = utc.Kind switch
        {
            DateTimeKind.Utc => utc,
            DateTimeKind.Local => utc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utc, DateTimeKind.Utc)
        };
        return new CalDateTime(u, "UTC");
    }

    private static DateTime FromCalDateTime(CalDateTime dt, bool isAllDay)
    {
        if (isAllDay || !dt.HasTime)
        {
            var local = new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, DateTimeKind.Local);
            return local.ToUniversalTime();
        }

        if (dt.IsUtc || string.Equals(dt.TzId, "UTC", StringComparison.OrdinalIgnoreCase))
            return DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);

        try { return dt.AsUtc; }
        catch { return DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc); }
    }

    private static string ToByDay(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "SU"
    };
}
