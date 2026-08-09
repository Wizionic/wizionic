using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using App.Core.Storage;
using App.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace App.Shared.Services.Tools;

/// <summary>
/// Native calendar tools: list calendars/events, add/update/delete events.
/// Resolves <see cref="ICalendarStore"/> at call time (same DI pattern as GalleryToolModule).
/// </summary>
public sealed class CalendarToolModule : IToolModule
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IToolExecutionTrace _trace;

    public CalendarToolModule(
        IServiceProvider services,
        IServiceScopeFactory scopeFactory,
        IToolExecutionTrace trace)
    {
        _services = services;
        _scopeFactory = scopeFactory;
        _trace = trace;
    }

    public string ModuleName => "Calendar";
    public bool IsAvailable => true;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(ListCalendarsAsync,
            new AIFunctionFactoryOptions
            {
                Name = "list_calendars",
                Description =
                    "List the user's calendars (id, name, color, visible). " +
                    "Use before add_calendar_event when the user names a specific calendar."
            }),
        AIFunctionFactory.Create(ListEventsAsync,
            new AIFunctionFactoryOptions
            {
                Name = "list_events",
                Description =
                    "List calendar events in a local date/time range. " +
                    "Use when the user asks what is on their schedule, free/busy, or upcoming events."
            }),
        AIFunctionFactory.Create(AddCalendarEventAsync,
            new AIFunctionFactoryOptions
            {
                Name = "add_calendar_event",
                Description =
                    "Create a calendar event (one-time or repeating). " +
                    "Use for add/schedule/book appointments, meetings, classes, sports, etc. " +
                    "Times are interpreted in the user's local timezone. " +
                    "For weekly repeats, set start_local to the first occurrence and repeat=weekly " +
                    "(or set weekday, e.g. Wednesday). " +
                    "Example: every Wednesday 9:30–12:30 table tennis → summary, start_local next Wed, end_local, location, repeat=weekly."
            }),
        AIFunctionFactory.Create(UpdateCalendarEventAsync,
            new AIFunctionFactoryOptions
            {
                Name = "update_calendar_event",
                Description =
                    "Update an existing calendar event by event_id (from list_events). " +
                    "Only pass fields you want to change."
            }),
        AIFunctionFactory.Create(DeleteCalendarEventAsync,
            new AIFunctionFactoryOptions
            {
                Name = "delete_calendar_event",
                Description = "Delete (soft-delete) a calendar event by event_id from list_events."
            })
    ];

    private CalendarWorkScope OpenScope()
    {
        try
        {
            var store = _services.GetService<ICalendarStore>();
            if (store != null)
            {
                return new CalendarWorkScope(
                    store,
                    _services.GetService<ICalendarSyncBridge>(),
                    owned: null);
            }
        }
        catch
        {
            // Singleton module resolving scoped store
        }

        var scope = _scopeFactory.CreateScope();
        return new CalendarWorkScope(
            scope.ServiceProvider.GetRequiredService<ICalendarStore>(),
            scope.ServiceProvider.GetService<ICalendarSyncBridge>(),
            scope);
    }

    private sealed class CalendarWorkScope : IDisposable
    {
        public ICalendarStore Store { get; }
        public ICalendarSyncBridge? Sync { get; }
        private readonly IServiceScope? _owned;

        public CalendarWorkScope(ICalendarStore store, ICalendarSyncBridge? sync, IServiceScope? owned)
        {
            Store = store;
            Sync = sync;
            _owned = owned;
        }

        public void Dispose() => _owned?.Dispose();
    }

    [Description("List calendars available to the user.")]
    private async Task<string> ListCalendarsAsync()
    {
        _trace.Record("📅 list_calendars()");
        try
        {
            using var work = OpenScope();
            await work.Store.EnsureDefaultCalendarAsync();
            var cals = await work.Store.LoadCalendarsAsync();
            if (cals.Count == 0)
                return "No calendars yet. add_calendar_event will create a Personal calendar if needed.";

            var sb = new StringBuilder();
            sb.AppendLine("Calendars:");
            foreach (var c in cals.OrderBy(x => x.SortOrder).ThenBy(x => x.Name))
            {
                var vis = c.IsVisible ? "visible" : "hidden";
                sb.AppendLine($"- id={c.Id} name=\"{c.Name}\" color={c.Color} {vis}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "Failed to list calendars: " + ex.Message;
        }
    }

    [Description("List events in a date range (local times).")]
    private async Task<string> ListEventsAsync(
        [Description("Range start local, e.g. 2026-08-09 or 2026-08-09 00:00")] string from_local,
        [Description("Range end local (exclusive preferred), e.g. 2026-08-16 or 2026-09-01")] string to_local,
        [Description("Optional calendar id or name filter.")] string? calendar = null)
    {
        _trace.Record($"📅 list_events(from={from_local}, to={to_local}, cal={calendar ?? "*"})");
        try
        {
            if (!TryParseLocalDateTime(from_local, out var fromLocal))
                return "list_events failed: could not parse from_local. Use e.g. 2026-08-09 or 2026-08-09 09:30.";
            if (!TryParseLocalDateTime(to_local, out var toLocal))
                return "list_events failed: could not parse to_local.";

            // If only a date was given for "to", treat as exclusive next day end of day → exclusive midnight next day
            if (LooksLikeDateOnly(to_local) && toLocal.TimeOfDay == TimeSpan.Zero)
                toLocal = toLocal.Date; // already date
            else if (LooksLikeDateOnly(to_local))
                toLocal = toLocal.Date.AddDays(1);

            var fromUtc = DateTime.SpecifyKind(fromLocal, DateTimeKind.Local).ToUniversalTime();
            var toUtc = DateTime.SpecifyKind(toLocal, DateTimeKind.Local).ToUniversalTime();
            if (toUtc <= fromUtc)
                toUtc = fromUtc.AddDays(7);

            using var work = OpenScope();
            await work.Store.EnsureDefaultCalendarAsync();
            var cals = await work.Store.LoadCalendarsAsync();
            var colorBy = cals.ToDictionary(c => c.Id, c => c.Color, StringComparer.OrdinalIgnoreCase);
            var nameBy = cals.ToDictionary(c => c.Id, c => c.Name, StringComparer.OrdinalIgnoreCase);

            string? calFilterId = null;
            if (!string.IsNullOrWhiteSpace(calendar))
            {
                calFilterId = ResolveCalendarId(cals, calendar);
                if (calFilterId is null)
                    return $"No calendar matched \"{calendar}\". Call list_calendars.";
            }

            // Wide index so RRULE masters are included
            var index = await work.Store.LoadEventIndexAsync(fromUtc.AddMonths(-1), toUtc.AddYears(2));
            if (calFilterId is not null)
                index = index.Where(e => string.Equals(e.CalendarId, calFilterId, StringComparison.OrdinalIgnoreCase)).ToList();

            var occs = CalendarIcs.ExpandOccurrences(index, colorBy, fromUtc, toUtc)
                .OrderBy(o => o.StartUtc)
                .Take(80)
                .ToList();

            if (occs.Count == 0)
                return $"No events from {fromLocal:g} to {toLocal:g} local.";

            var sb = new StringBuilder();
            sb.AppendLine($"Events ({occs.Count}, local times):");
            foreach (var o in occs)
            {
                var calName = nameBy.GetValueOrDefault(o.CalendarId, "?");
                var start = o.StartUtc.ToLocalTime();
                var end = o.EndUtc.ToLocalTime();
                var when = o.IsAllDay
                    ? $"{start:yyyy-MM-dd} (all day)"
                    : $"{start:yyyy-MM-dd HH:mm}–{end:HH:mm}";
                var loc = string.IsNullOrEmpty(o.Location) ? "" : $" @ {o.Location}";
                var rep = o.IsRecurring ? " [recurring]" : "";
                sb.AppendLine($"- event_id={o.EventId} cal=\"{calName}\" {when} \"{o.Summary}\"{loc}{rep}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "Failed to list events: " + ex.Message;
        }
    }

    [Description("Create a calendar event.")]
    private async Task<string> AddCalendarEventAsync(
        [Description("Event title, e.g. Play table tennis")] string summary,
        [Description("Start local datetime: 2026-08-13 09:30 or 2026-08-13T09:30")] string start_local,
        [Description("End local datetime: 2026-08-13 12:30")] string end_local,
        [Description("Optional location")] string? location = null,
        [Description("Optional description / notes")] string? description = null,
        [Description("Calendar name or id; default Personal")] string? calendar = null,
        [Description("true for all-day events (date-only start/end)")] bool all_day = false,
        [Description("none|daily|weekly|weekdays|monthly|yearly")] string? repeat = "none",
        [Description("For weekly: Monday..Sunday or MO..SU; adjusts start to next that weekday if needed")] string? weekday = null,
        [Description("Optional raw RRULE body without RRULE: prefix; overrides repeat if set")] string? rrule = null)
    {
        _trace.Record(
            $"📅 add_calendar_event(summary=\"{summary}\", start={start_local}, end={end_local}, " +
            $"repeat={repeat}, weekday={weekday ?? "-"}, all_day={all_day})");

        if (string.IsNullOrWhiteSpace(summary))
            return "add_calendar_event failed: summary is required.";

        try
        {
            if (!TryParseLocalDateTime(start_local, out var startLocal))
                return "add_calendar_event failed: could not parse start_local.";
            if (!TryParseLocalDateTime(end_local, out var endLocal))
                return "add_calendar_event failed: could not parse end_local.";

            if (!string.IsNullOrWhiteSpace(weekday))
            {
                if (!TryParseWeekday(weekday, out var dow))
                    return $"add_calendar_event failed: unknown weekday \"{weekday}\".";
                // Snap start to next occurrence of that weekday (including today if same day)
                var days = ((int)dow - (int)startLocal.DayOfWeek + 7) % 7;
                if (days != 0)
                {
                    startLocal = startLocal.AddDays(days);
                    endLocal = endLocal.AddDays(days);
                }
            }

            if (all_day)
            {
                startLocal = startLocal.Date;
                if (endLocal.Date <= startLocal)
                    endLocal = startLocal.AddDays(1);
                else if (endLocal.TimeOfDay != TimeSpan.Zero || endLocal.Date == startLocal)
                    endLocal = endLocal.Date.AddDays(1); // exclusive end
            }
            else if (endLocal <= startLocal)
            {
                endLocal = startLocal.AddHours(1);
            }

            using var work = OpenScope();
            await work.Store.EnsureDefaultCalendarAsync();
            var cals = await work.Store.LoadCalendarsAsync();
            var calId = ResolveCalendarId(cals, calendar)
                        ?? cals.FirstOrDefault(c => c.IsVisible)?.Id
                        ?? cals.FirstOrDefault()?.Id;
            if (calId is null)
            {
                var id = Guid.NewGuid().ToString("N");
                await work.Store.CreateCalendarAsync(id, CalendarConstants.DefaultCalendarName, CalendarConstants.DefaultCalendarColor);
                work.Sync?.ScheduleAutoSyncCalendarAfterLocalSave(id);
                calId = id;
            }

            string? rule = null;
            if (!string.IsNullOrWhiteSpace(rrule))
            {
                rule = rrule.Trim();
                if (rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
                    rule = rule["RRULE:".Length..].Trim();
            }
            else
            {
                rule = CalendarIcs.BuildRRule(repeat ?? "none", startLocal);
            }

            var now = DateTime.UtcNow;
            DateTime startUtc, endUtc;
            if (all_day)
            {
                startUtc = DateTime.SpecifyKind(startLocal.Date, DateTimeKind.Local).ToUniversalTime();
                endUtc = DateTime.SpecifyKind(endLocal.Date, DateTimeKind.Local).ToUniversalTime();
            }
            else
            {
                startUtc = DateTime.SpecifyKind(startLocal, DateTimeKind.Local).ToUniversalTime();
                endUtc = DateTime.SpecifyKind(endLocal, DateTimeKind.Local).ToUniversalTime();
            }

            var evt = new CalendarEvent(
                Id: Guid.NewGuid().ToString("N"),
                CalendarId: calId,
                Summary: summary.Trim(),
                StartUtc: startUtc,
                EndUtc: endUtc,
                IsAllDay: all_day,
                Description: string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Location: string.IsNullOrWhiteSpace(location) ? null : location.Trim(),
                TimeZoneId: TimeZoneInfo.Local.Id,
                RRule: rule,
                CreatedUtc: now,
                ModifiedUtc: now);

            await work.Store.UpsertEventAsync(evt);
            work.Sync?.ScheduleAutoSyncEventAfterLocalSave(evt.CalendarId, evt.Id);

            var calName = (await work.Store.LoadCalendarsAsync())
                .FirstOrDefault(c => c.Id == calId)?.Name ?? "calendar";
            var when = all_day
                ? $"{startLocal:yyyy-MM-dd} (all day)"
                : $"{startLocal:yyyy-MM-dd HH:mm}–{endLocal:HH:mm} local";
            var rep = rule is null ? "does not repeat" : CalendarIcs.RRulePresetLabel(rule, startLocal);
            var loc = string.IsNullOrEmpty(evt.Location) ? "" : $" at {evt.Location}";

            _trace.Record($"   ✅ created event_id={evt.Id}");
            return $"Added event_id={evt.Id} to \"{calName}\": \"{evt.Summary}\" {when}{loc}; {rep}.";
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "add_calendar_event failed: " + ex.Message;
        }
    }

    [Description("Update fields on an existing event.")]
    private async Task<string> UpdateCalendarEventAsync(
        [Description("event_id from list_events")] string event_id,
        [Description("New title")] string? summary = null,
        [Description("New start local datetime")] string? start_local = null,
        [Description("New end local datetime")] string? end_local = null,
        [Description("New location")] string? location = null,
        [Description("New description")] string? description = null,
        [Description("none|daily|weekly|weekdays|monthly|yearly")] string? repeat = null,
        [Description("true/false all-day")] bool? all_day = null)
    {
        _trace.Record($"📅 update_calendar_event(id={event_id})");
        if (string.IsNullOrWhiteSpace(event_id))
            return "update_calendar_event failed: event_id is required.";

        try
        {
            using var work = OpenScope();
            var existing = await work.Store.LoadEventAsync(event_id);
            if (existing is null)
                return $"No event with id={event_id}.";

            var startLocal = existing.StartUtc.ToLocalTime();
            var endLocal = existing.EndUtc.ToLocalTime();
            var isAllDay = all_day ?? existing.IsAllDay;

            if (!string.IsNullOrWhiteSpace(start_local) && TryParseLocalDateTime(start_local, out var s))
                startLocal = s;
            if (!string.IsNullOrWhiteSpace(end_local) && TryParseLocalDateTime(end_local, out var e))
                endLocal = e;

            string? rule = existing.RRule;
            if (repeat is not null)
                rule = CalendarIcs.BuildRRule(repeat, startLocal);

            DateTime startUtc, endUtc;
            if (isAllDay)
            {
                startUtc = DateTime.SpecifyKind(startLocal.Date, DateTimeKind.Local).ToUniversalTime();
                var endDate = endLocal.Date;
                if (endDate <= startLocal.Date) endDate = startLocal.Date.AddDays(1);
                else if (endLocal.TimeOfDay != TimeSpan.Zero) endDate = endLocal.Date.AddDays(1);
                endUtc = DateTime.SpecifyKind(endDate, DateTimeKind.Local).ToUniversalTime();
            }
            else
            {
                startUtc = DateTime.SpecifyKind(startLocal, DateTimeKind.Local).ToUniversalTime();
                endUtc = DateTime.SpecifyKind(endLocal, DateTimeKind.Local).ToUniversalTime();
                if (endUtc <= startUtc) endUtc = startUtc.AddHours(1);
            }

            var updated = existing with
            {
                Summary = string.IsNullOrWhiteSpace(summary) ? existing.Summary : summary.Trim(),
                StartUtc = startUtc,
                EndUtc = endUtc,
                IsAllDay = isAllDay,
                Location = location is null ? existing.Location : (string.IsNullOrWhiteSpace(location) ? null : location.Trim()),
                Description = description is null ? existing.Description : (string.IsNullOrWhiteSpace(description) ? null : description.Trim()),
                RRule = rule,
                Sequence = existing.Sequence + 1,
                ModifiedUtc = DateTime.UtcNow,
                DeletedAt = null
            };

            await work.Store.UpsertEventAsync(updated);
            work.Sync?.ScheduleAutoSyncEventAfterLocalSave(updated.CalendarId, updated.Id);
            _trace.Record($"   ✅ updated {event_id}");
            return $"Updated event_id={event_id}: \"{updated.Summary}\".";
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "update_calendar_event failed: " + ex.Message;
        }
    }

    [Description("Delete a calendar event.")]
    private async Task<string> DeleteCalendarEventAsync(
        [Description("event_id from list_events")] string event_id)
    {
        _trace.Record($"📅 delete_calendar_event(id={event_id})");
        if (string.IsNullOrWhiteSpace(event_id))
            return "delete_calendar_event failed: event_id is required.";

        try
        {
            using var work = OpenScope();
            var existing = await work.Store.LoadEventAsync(event_id);
            if (existing is null)
                return $"No event with id={event_id}.";

            var deletedAt = await work.Store.DeleteEventAsync(event_id);
            work.Sync?.ScheduleAutoSyncEventDeleteAfterLocalDelete(existing.CalendarId, event_id, deletedAt);
            _trace.Record($"   ✅ deleted {event_id}");
            return $"Deleted event_id={event_id} (\"{existing.Summary}\").";
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "delete_calendar_event failed: " + ex.Message;
        }
    }

    // ── Parsing helpers ────────────────────────────────────────────────────

    private static string? ResolveCalendarId(List<LocalCalendar> cals, string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        var q = nameOrId.Trim();
        var byId = cals.FirstOrDefault(c => string.Equals(c.Id, q, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId.Id;
        var exact = cals.FirstOrDefault(c => string.Equals(c.Name, q, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Id;
        var partial = cals.FirstOrDefault(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        return partial?.Id;
    }

    private static bool LooksLikeDateOnly(string s)
    {
        s = s.Trim();
        return Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$")
               || Regex.IsMatch(s, @"^\d{1,2}/\d{1,2}/\d{4}$");
    }

    private static bool TryParseLocalDateTime(string input, out DateTime local)
    {
        local = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();

        string[] formats =
        [
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd H:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd",
            "M/d/yyyy h:mm tt",
            "M/d/yyyy H:mm",
            "M/d/yyyy",
            "MM/dd/yyyy HH:mm",
            "MM/dd/yyyy"
        ];

        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Local);
            return true;
        }

        if (DateTime.TryParse(s, CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Local);
            return true;
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Local);
            return true;
        }

        return false;
    }

    private static bool TryParseWeekday(string input, out DayOfWeek dow)
    {
        dow = default;
        var s = input.Trim().ToUpperInvariant();
        return s switch
        {
            "SU" or "SUN" or "SUNDAY" => Assign(DayOfWeek.Sunday, out dow),
            "MO" or "MON" or "MONDAY" => Assign(DayOfWeek.Monday, out dow),
            "TU" or "TUE" or "TUESDAY" => Assign(DayOfWeek.Tuesday, out dow),
            "WE" or "WED" or "WEDNESDAY" => Assign(DayOfWeek.Wednesday, out dow),
            "TH" or "THU" or "THURSDAY" => Assign(DayOfWeek.Thursday, out dow),
            "FR" or "FRI" or "FRIDAY" => Assign(DayOfWeek.Friday, out dow),
            "SA" or "SAT" or "SATURDAY" => Assign(DayOfWeek.Saturday, out dow),
            _ => Enum.TryParse(input, true, out dow)
        };

        static bool Assign(DayOfWeek d, out DayOfWeek result)
        {
            result = d;
            return true;
        }
    }
}
