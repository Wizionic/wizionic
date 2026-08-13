namespace App.Core.Workflows;

/// <summary>
/// Minimal 5-field cron (minute hour day-of-month month day-of-week).
/// Supports *, numbers, lists (1,2,3), ranges (1-5), steps (*/15, 1-10/2).
/// Day-of-week: 0 or 7 = Sunday … 6 = Saturday.
/// </summary>
public static class CronExpression
{
    /// <summary>
    /// Build cron (or once-token) from calendar-style start + repeat preset.
    /// Presets: none, daily, weekly, weekdays, monthly, yearly.
    /// For <c>none</c>, returns type "once" with expression = local ISO datetime.
    /// </summary>
    public static (string Type, string Expression) FromStartAndRepeat(DateTime startLocal, string repeatPreset)
    {
        var m = startLocal.Minute;
        var h = startLocal.Hour;
        var day = startLocal.Day;
        var month = startLocal.Month;
        var dow = (int)startLocal.DayOfWeek; // 0=Sun

        return (repeatPreset ?? "none").ToLowerInvariant() switch
        {
            "daily" => ("cron", $"{m} {h} * * *"),
            "weekly" => ("cron", $"{m} {h} * * {dow}"),
            "weekdays" => ("cron", $"{m} {h} * * 1-5"),
            "monthly" => ("cron", $"{m} {h} {day} * *"),
            "yearly" => ("cron", $"{m} {h} {day} {month} *"),
            // Single fire at this local wall time
            _ => ("once", startLocal.ToString("yyyy-MM-dd'T'HH:mm"))
        };
    }

    /// <summary>Map cron expression + a sample start to calendar repeat preset (best-effort).</summary>
    public static string ToRepeatPreset(string? triggerType, string? expression, DateTime sampleLocal)
    {
        if (string.Equals(triggerType, "once", StringComparison.OrdinalIgnoreCase)
            || string.Equals(triggerType, "manual", StringComparison.OrdinalIgnoreCase))
            return "none";
        if (string.IsNullOrWhiteSpace(expression)) return "none";

        var p = expression.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 5) return "custom";

        // m h dom mon dow
        var dom = p[2];
        var mon = p[3];
        var dow = p[4];

        if (dom == "*" && mon == "*" && dow == "*") return "daily";
        if (dom == "*" && mon == "*" && (dow == "1-5" || dow == "1,2,3,4,5")) return "weekdays";
        if (dom == "*" && mon == "*" && dow is not "*") return "weekly";
        if (dom is not "*" && mon == "*" && dow == "*") return "monthly";
        if (dom is not "*" && mon is not "*" && dow == "*") return "yearly";
        return "custom";
    }

    /// <summary>Parse once expression (yyyy-MM-ddTHH:mm local) for due checks.</summary>
    public static bool TryParseOnceLocal(string? expression, out DateTime local)
    {
        local = default;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        string[] formats = ["yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss"];
        if (DateTime.TryParseExact(expression.Trim(), formats, null,
                System.Globalization.DateTimeStyles.None, out var dt)
            || DateTime.TryParse(expression, out dt))
        {
            local = DateTime.SpecifyKind(dt, DateTimeKind.Local);
            return true;
        }
        return false;
    }

    public static bool IsDue(string expression, DateTime localNow, DateTime? lastRunLocal)
    {
        if (!TryGetNext(expression, localNow.AddMinutes(-1), out var next))
            return false;
        // Due if next fire is at or before this minute and we haven't run at this minute
        if (next > localNow) return false;
        if (lastRunLocal is DateTime lr &&
            lr.Year == localNow.Year && lr.Month == localNow.Month && lr.Day == localNow.Day &&
            lr.Hour == localNow.Hour && lr.Minute == localNow.Minute)
            return false;
        // Fire if we're in the same minute as a scheduled slot
        return Matches(expression, localNow);
    }

    public static bool Matches(string expression, DateTime local)
    {
        if (!TryParse(expression, out var parts))
            return false;
        return MatchField(parts[0], local.Minute, 0, 59)
            && MatchField(parts[1], local.Hour, 0, 23)
            && MatchField(parts[2], local.Day, 1, 31)
            && MatchField(parts[3], local.Month, 1, 12)
            && MatchField(parts[4], (int)local.DayOfWeek, 0, 7, sundayDual: true);
    }

    /// <summary>Next matching local time strictly after <paramref name="afterLocal"/> (minute resolution).</summary>
    public static bool TryGetNext(string expression, DateTime afterLocal, out DateTime nextLocal)
    {
        nextLocal = default;
        if (!TryParse(expression, out _))
            return false;

        var t = new DateTime(afterLocal.Year, afterLocal.Month, afterLocal.Day, afterLocal.Hour, afterLocal.Minute, 0, DateTimeKind.Local)
            .AddMinutes(1);
        // Search up to ~2 years of minutes is too much; scan hour by hour then minute
        for (var i = 0; i < 366 * 24 * 60; i++)
        {
            if (Matches(expression, t))
            {
                nextLocal = t;
                return true;
            }
            t = t.AddMinutes(1);
        }
        return false;
    }

    /// <summary>Next N occurrences after now (local).</summary>
    public static IReadOnlyList<DateTime> NextOccurrences(string expression, DateTime afterLocal, int count)
    {
        var list = new List<DateTime>();
        var cursor = afterLocal;
        for (var n = 0; n < count; n++)
        {
            if (!TryGetNext(expression, cursor, out var next))
                break;
            list.Add(next);
            cursor = next;
        }
        return list;
    }

    private static bool TryParse(string expression, out string[] parts)
    {
        parts = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var p = expression.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 5) return false;
        parts = p;
        return true;
    }

    private static bool MatchField(string field, int value, int min, int max, bool sundayDual = false)
    {
        if (field == "*") return true;
        foreach (var piece in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MatchPiece(piece, value, min, max, sundayDual))
                return true;
        }
        return false;
    }

    private static bool MatchPiece(string piece, int value, int min, int max, bool sundayDual)
    {
        var step = 1;
        var body = piece;
        var slash = piece.IndexOf('/');
        if (slash >= 0)
        {
            body = piece[..slash];
            if (!int.TryParse(piece[(slash + 1)..], out step) || step <= 0)
                return false;
        }

        if (body == "*")
        {
            for (var v = min; v <= max; v += step)
            {
                if (ValueEquals(v, value, sundayDual)) return true;
            }
            return false;
        }

        var dash = body.IndexOf('-');
        if (dash >= 0)
        {
            if (!int.TryParse(body[..dash], out var a) || !int.TryParse(body[(dash + 1)..], out var b))
                return false;
            for (var v = a; v <= b; v += step)
            {
                if (ValueEquals(v, value, sundayDual)) return true;
            }
            return false;
        }

        if (!int.TryParse(body, out var n)) return false;
        if (step > 1)
            return ValueEquals(n, value, sundayDual) || (value >= n && (value - n) % step == 0 && value <= max);
        return ValueEquals(n, value, sundayDual);
    }

    private static bool ValueEquals(int fieldVal, int actual, bool sundayDual)
    {
        if (!sundayDual) return fieldVal == actual;
        // 0 and 7 both Sunday
        if (fieldVal == 7) fieldVal = 0;
        if (actual == 7) actual = 0;
        return fieldVal == actual;
    }
}
