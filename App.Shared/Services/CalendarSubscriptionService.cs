using App.Core.Storage;

namespace App.Shared.Services;

public sealed class CalendarSubscriptionService
{
    private readonly ICalendarStore _store;
    private readonly ICalendarIcsFetcher _fetcher;
    private readonly ICalendarSyncBridge? _sync;

    public CalendarSubscriptionService(
        ICalendarStore store,
        ICalendarIcsFetcher fetcher,
        ICalendarSyncBridge? sync = null)
    {
        _store = store;
        _fetcher = fetcher;
        _sync = sync;
    }

    public async Task<(bool Ok, string Message)> SubscribeAsync(string url, CancellationToken ct = default)
    {
        var normalized = CalendarIcs.NormalizeFeedUrl(url);
        if (!CalendarIcs.IsAllowedFeedUrl(normalized))
            return (false, "Enter an https (or webcal) calendar URL.");

        CalendarIcsFetchResult fetched;
        try
        {
            fetched = await _fetcher.FetchAsync(normalized, ct: ct);
        }
        catch (Exception ex)
        {
            return (false, "Could not fetch the calendar: " + ex.Message);
        }

        if (fetched.StatusCode == 304 || string.IsNullOrWhiteSpace(fetched.Text))
        {
            if (fetched.StatusCode is >= 200 and < 300 && string.IsNullOrWhiteSpace(fetched.Text))
                return (false, "The URL returned an empty calendar.");
            if (fetched.StatusCode is < 200 or >= 300)
                return (false, $"Could not fetch the calendar ({fetched.StatusCode}).");
        }

        var id = Guid.NewGuid().ToString("N");
        var parsed = CalendarIcs.Import(fetched.Text ?? "", id);
        var name = string.IsNullOrWhiteSpace(parsed.CalendarName) ? "Subscribed calendar" : parsed.CalendarName.Trim();
        var refreshMins = parsed.RefreshInterval is { } ts
            ? CalendarConstants.ClampRefreshMinutes(Math.Max(1, (int)Math.Round(ts.TotalMinutes)))
            : CalendarConstants.DefaultSubscriptionRefreshMinutes;

        var color = CalendarConstants.ColorForIndex((await _store.LoadCalendarsAsync(ct)).Count);
        await _store.CreateCalendarAsync(id, name, color, parsed.Description, ct);
        await _store.SetCalendarSubscriptionAsync(
            id, normalized, refreshMins, fetched.Etag, fetched.LastModified, DateTime.UtcNow, ct);

        await ApplySnapshotAsync(id, parsed.Events, ct);
        _sync?.ScheduleAutoSyncCalendarAfterLocalSave(id);
        return (true, $"Subscribed to {name} ({parsed.Events.Count} event(s)).");
    }

    public async Task<(bool Ok, string Message)> RefreshAsync(string calendarId, CancellationToken ct = default)
    {
        var cals = await _store.LoadCalendarsAsync(ct);
        var cal = cals.FirstOrDefault(c => string.Equals(c.Id, calendarId, StringComparison.OrdinalIgnoreCase));
        if (cal is null || !cal.IsSubscribed)
            return (false, "That calendar is not a subscription.");

        CalendarIcsFetchResult fetched;
        try
        {
            fetched = await _fetcher.FetchAsync(cal.SubscriptionUrl!, cal.SubscriptionEtag, cal.SubscriptionLastModified, ct);
        }
        catch (Exception ex)
        {
            return (false, "Refresh failed: " + ex.Message);
        }

        if (fetched.StatusCode == 304)
        {
            await _store.SetCalendarSubscriptionAsync(
                cal.Id, cal.SubscriptionUrl, cal.RefreshIntervalMinutes,
                cal.SubscriptionEtag, cal.SubscriptionLastModified, DateTime.UtcNow, ct);
            return (true, "No changes.");
        }

        if (fetched.StatusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(fetched.Text))
            return (false, $"Refresh failed ({fetched.StatusCode}).");

        var parsed = CalendarIcs.Import(fetched.Text, cal.Id);
        var refreshMins = parsed.RefreshInterval is { } ts
            ? CalendarConstants.ClampRefreshMinutes(Math.Max(1, (int)Math.Round(ts.TotalMinutes)))
            : cal.RefreshIntervalMinutes ?? CalendarConstants.DefaultSubscriptionRefreshMinutes;

        if (!string.IsNullOrWhiteSpace(parsed.CalendarName)
            && !string.Equals(parsed.CalendarName.Trim(), cal.Name, StringComparison.Ordinal))
        {
            await _store.UpdateCalendarAsync(cal.Id, parsed.CalendarName.Trim(), cal.Color, cal.IsVisible, parsed.Description ?? cal.Description, ct);
        }

        await ApplySnapshotAsync(cal.Id, parsed.Events, ct);
        await _store.SetCalendarSubscriptionAsync(
            cal.Id, cal.SubscriptionUrl, refreshMins, fetched.Etag, fetched.LastModified, DateTime.UtcNow, ct);
        _sync?.ScheduleAutoSyncCalendarAfterLocalSave(cal.Id);
        return (true, $"Updated {parsed.Events.Count} event(s).");
    }

    public async Task RefreshDueAsync(CancellationToken ct = default)
    {
        var cals = await _store.LoadCalendarsAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var cal in cals.Where(c => c.IsSubscribed))
        {
            ct.ThrowIfCancellationRequested();
            var interval = TimeSpan.FromMinutes(
                CalendarConstants.ClampRefreshMinutes(cal.RefreshIntervalMinutes ?? CalendarConstants.DefaultSubscriptionRefreshMinutes));
            if (cal.LastFetchedUtc is { } last && now - last < interval)
                continue;
            try { await RefreshAsync(cal.Id, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Console.WriteLine($"[CalendarSub] refresh {cal.Id}: {ex.Message}"); }
        }
    }

    private async Task ApplySnapshotAsync(string calendarId, IReadOnlyList<CalendarEvent> incoming, CancellationToken ct)
    {
        var existing = await _store.LoadEventsForCalendarAsync(calendarId, ct);
        var byId = existing.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in incoming)
        {
            byId.TryGetValue(evt.Id, out var prev);
            var merged = CalendarReminder.CopyLocalReminders(evt, prev);
            await _store.UpsertEventAsync(merged, ct);
            keep.Add(merged.Id);
        }

        foreach (var old in existing)
        {
            if (keep.Contains(old.Id))
                continue;
            await _store.DeleteEventAsync(old.Id, ct);
        }
    }
}
