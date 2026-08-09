namespace App.Core.Storage;

/// <summary>Reserved calendar defaults and palette.</summary>
public static class CalendarConstants
{
    public const string DefaultCalendarName = "Personal";

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

    public static string ColorForIndex(int index)
    {
        if (Palette.Length == 0) return DefaultCalendarColor;
        var i = index % Palette.Length;
        if (i < 0) i += Palette.Length;
        return Palette[i];
    }
}
