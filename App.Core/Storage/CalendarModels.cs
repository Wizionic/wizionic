namespace App.Core.Storage;

/// <summary>Calendar container (VCALENDAR-like) for multi-calendar layering.</summary>
public record LocalCalendar(
    string Id,
    string Name,
    string Color,
    DateTime LastUpdated,
    string? Description = null,
    string? TimeZone = null,
    bool IsVisible = true,
    int SortOrder = 0,
    /// <summary>When true, this calendar is reserved for future AI workflow triggers.</summary>
    bool IsWorkflowCalendar = false,
    /// <summary>HTTPS (or normalized webcal) ICS feed. Null for a local calendar.</summary>
    string? SubscriptionUrl = null,
    /// <summary>Poll period in minutes. Null = default (6 hours) for subscribed calendars.</summary>
    int? RefreshIntervalMinutes = null,
    string? SubscriptionEtag = null,
    string? SubscriptionLastModified = null,
    DateTime? LastFetchedUtc = null)
{
    public bool IsSubscribed => !string.IsNullOrWhiteSpace(SubscriptionUrl);
}

/// <summary>
/// Single calendar event (VEVENT-aligned). Times are stored in UTC;
/// <see cref="TimeZoneId"/> preserves export/display fidelity.
/// </summary>
public record CalendarEvent(
    string Id,
    string CalendarId,
    string Summary,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsAllDay = false,
    string? Description = null,
    string? Location = null,
    string? TimeZoneId = null,
    /// <summary>RFC 5545 RRULE body (without the "RRULE:" prefix), e.g. FREQ=WEEKLY;BYDAY=MO.</summary>
    string? RRule = null,
    IReadOnlyList<DateTime>? RDates = null,
    IReadOnlyList<DateTime>? ExDates = null,
    /// <summary>CONFIRMED | TENTATIVE | CANCELLED</summary>
    string Status = "CONFIRMED",
    /// <summary>OPAQUE | TRANSPARENT</summary>
    string Transparency = "OPAQUE",
    int Sequence = 0,
    DateTime? CreatedUtc = null,
    DateTime? ModifiedUtc = null,
    DateTime? DeletedAt = null,
    /// <summary>RECURRENCE-ID for exception instances of a recurring series.</summary>
    DateTime? RecurrenceId = null,
    /// <summary>Future AI workflow trigger hook (X-WIZIONIC-WORKFLOW).</summary>
    string? WorkflowId = null,
    /// <summary>Minutes before start (0 = at start). Null = no alert.</summary>
    int? ReminderMinutesBefore = null,
    /// <summary>Web Audio preset id (chime, ping, …).</summary>
    string? ReminderSoundId = null,
    /// <summary>sound now; sms/email later.</summary>
    string? ReminderChannel = null,
    /// <summary>How long the alarm keeps repeating (1 or 5). Default 1.</summary>
    int? ReminderRepeatMinutes = null);

/// <summary>Lightweight event row for month/week grids (no encrypted description body).</summary>
public record CalendarEventIndex(
    string Id,
    string CalendarId,
    string Summary,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsAllDay,
    string Status,
    DateTime? ModifiedUtc,
    DateTime? DeletedAt,
    string? RRule = null,
    string? Location = null,
    string? WorkflowId = null,
    int? ReminderMinutesBefore = null,
    string? ReminderSoundId = null,
    int? ReminderRepeatMinutes = null)
{
    public static CalendarEventIndex FromEvent(CalendarEvent e) => new(
        e.Id,
        e.CalendarId,
        e.Summary,
        e.StartUtc,
        e.EndUtc,
        e.IsAllDay,
        e.Status,
        e.ModifiedUtc,
        e.DeletedAt,
        e.RRule,
        e.Location,
        e.WorkflowId,
        e.ReminderMinutesBefore,
        e.ReminderSoundId,
        e.ReminderRepeatMinutes);
}

/// <summary>Expanded occurrence for a visible date range (recurrence expansion).</summary>
public record CalendarOccurrence(
    string EventId,
    string CalendarId,
    string Summary,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsAllDay,
    string Color,
    string? Location = null,
    bool IsRecurring = false,
    DateTime? RecurrenceId = null);
