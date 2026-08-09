namespace App.Core.Storage;

/// <summary>
/// Last-write-wins merge for calendar events by UID (Id).
/// </summary>
public static class CalendarSyncMerger
{
    public sealed record EventMergeResult(
        CalendarEvent Chosen,
        bool PreferRemote,
        bool Equal);

    public static EventMergeResult Merge(CalendarEvent local, CalendarEvent remote)
    {
        var localTicks = EffectiveTicks(local);
        var remoteTicks = EffectiveTicks(remote);

        if (localTicks == remoteTicks && ContentEquals(local, remote))
            return new EventMergeResult(local, PreferRemote: false, Equal: true);

        if (remoteTicks > localTicks)
            return new EventMergeResult(remote, PreferRemote: true, Equal: false);

        if (localTicks > remoteTicks)
            return new EventMergeResult(local, PreferRemote: false, Equal: false);

        // Tie-break: prefer non-deleted, then higher sequence, then remote for convergence.
        if (local.DeletedAt is null && remote.DeletedAt is not null)
            return new EventMergeResult(local, PreferRemote: false, Equal: false);
        if (remote.DeletedAt is null && local.DeletedAt is not null)
            return new EventMergeResult(remote, PreferRemote: true, Equal: false);
        if (remote.Sequence != local.Sequence)
            return remote.Sequence > local.Sequence
                ? new EventMergeResult(remote, PreferRemote: true, Equal: false)
                : new EventMergeResult(local, PreferRemote: false, Equal: false);

        return new EventMergeResult(remote, PreferRemote: true, Equal: false);
    }

    public static long EffectiveTicks(CalendarEvent e)
    {
        long max = 0;
        if (e.ModifiedUtc.HasValue) max = Math.Max(max, e.ModifiedUtc.Value.Ticks);
        if (e.DeletedAt.HasValue) max = Math.Max(max, e.DeletedAt.Value.Ticks);
        if (e.CreatedUtc.HasValue) max = Math.Max(max, e.CreatedUtc.Value.Ticks);
        max = Math.Max(max, e.StartUtc.Ticks);
        return max;
    }

    public static bool ContentEquals(CalendarEvent a, CalendarEvent b)
    {
        if (!string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.CalendarId, b.CalendarId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.Summary, b.Summary, StringComparison.Ordinal)) return false;
        if (a.StartUtc != b.StartUtc || a.EndUtc != b.EndUtc || a.IsAllDay != b.IsAllDay) return false;
        if (!string.Equals(a.Description, b.Description, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Location, b.Location, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.RRule, b.RRule, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Status, b.Status, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.DeletedAt != b.DeletedAt) return false;
        if (!string.Equals(a.WorkflowId, b.WorkflowId, StringComparison.Ordinal)) return false;
        return true;
    }
}
