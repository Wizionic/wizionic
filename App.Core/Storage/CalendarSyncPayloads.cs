using System.Text.Json;

namespace App.Core.Storage;

/// <summary>WebRTC payload for calendar meta (name, color, visibility).</summary>
public record CalendarMetaSyncPayload(
    string CalendarId,
    string Name,
    string Color,
    bool IsVisible,
    string? Description,
    long LastUpdatedTicks,
    bool IsWorkflowCalendar = false,
    string? SubscriptionUrl = null,
    int? RefreshIntervalMinutes = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(
        string calendarId,
        string name,
        string color,
        bool isVisible,
        string? description,
        long lastUpdatedTicks,
        bool isWorkflowCalendar = false,
        string? subscriptionUrl = null,
        int? refreshIntervalMinutes = null) =>
        JsonSerializer.Serialize(
            new CalendarMetaSyncPayload(
                calendarId, name, color, isVisible, description, lastUpdatedTicks, isWorkflowCalendar,
                subscriptionUrl, refreshIntervalMinutes),
            JsonOpts);

    public static CalendarMetaSyncPayload? Deserialize(string json)
    {
        try
        {
            var p = JsonSerializer.Deserialize<CalendarMetaSyncPayload>(json, JsonOpts);
            return string.IsNullOrEmpty(p?.CalendarId) ? null : p;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>WebRTC payload for a single calendar event (full VEVENT body).</summary>
public record CalendarEventSyncPayload(string CalendarId, CalendarEvent Event)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string CompositeId(string calendarId, string eventId) => $"{calendarId}/{eventId}";

    public static bool TrySplitCompositeId(string composite, out string calendarId, out string eventId)
    {
        calendarId = "";
        eventId = "";
        var idx = composite.IndexOf('/');
        if (idx <= 0 || idx >= composite.Length - 1) return false;
        calendarId = composite[..idx];
        eventId = composite[(idx + 1)..];
        return true;
    }

    public static string Serialize(string calendarId, CalendarEvent evt) =>
        JsonSerializer.Serialize(new CalendarEventSyncPayload(calendarId, evt), JsonOpts);

    public static CalendarEventSyncPayload? Deserialize(string json)
    {
        try
        {
            var p = JsonSerializer.Deserialize<CalendarEventSyncPayload>(json, JsonOpts);
            if (p is null || string.IsNullOrEmpty(p.CalendarId) || p.Event is null || string.IsNullOrEmpty(p.Event.Id))
                return null;
            return p;
        }
        catch
        {
            return null;
        }
    }
}
