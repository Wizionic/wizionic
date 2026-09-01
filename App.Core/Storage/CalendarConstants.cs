namespace App.Core.Storage;

/// <summary>Reserved calendar defaults and palette.</summary>
public static class CalendarConstants
{
    public const string DefaultCalendarName = "Personal";
    /// <summary>Stable id so devices do not each mint a different Personal calendar.</summary>
    public const string DefaultCalendarId = "wizionic-personal";

    /// <summary>System calendar for AI workflow schedule projection.</summary>
    public const string WorkflowCalendarName = "Workflows";
    public const string WorkflowCalendarColor = "#7c3aed";
    public const string WorkflowCalendarId = "wizionic-workflows";

    /// <summary>Google-calendar-like purple accent used for the default calendar.</summary>
    public const string DefaultCalendarColor = "#8E24AA";

    /// <summary>Suggested colors for new calendars (user can pick any).</summary>
    public static readonly string[] Palette =
    [
        "#8E24AA", // purple
        "#039BE5", // blue
        "#33B679", // green
        "#E67C73", // red/coral
        "#F6BF26", // yellow
        "#F4511E", // orange
        "#7986CB", // lavender
        "#616161", // grey
        "#0B8043", // dark green
        "#D50000", // bright red
    ];

    public const int DefaultSubscriptionRefreshMinutes = 360;
    public const int MinSubscriptionRefreshMinutes = 15;
    public const int MaxSubscriptionRefreshMinutes = 24 * 60;

    public static int ClampRefreshMinutes(int minutes) =>
        Math.Clamp(minutes, MinSubscriptionRefreshMinutes, MaxSubscriptionRefreshMinutes);

    public static string ColorForIndex(int index)
    {
        if (Palette.Length == 0) return DefaultCalendarColor;
        var i = index % Palette.Length;
        if (i < 0) i += Palette.Length;
        return Palette[i];
    }
}
