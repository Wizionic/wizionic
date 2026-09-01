namespace App.Core.Storage;

public record CalendarIcsFetchResult(
    int StatusCode,
    string? Text,
    string? Etag,
    string? LastModified);

public interface ICalendarIcsFetcher
{
    Task<CalendarIcsFetchResult> FetchAsync(
        string url,
        string? etag = null,
        string? lastModified = null,
        CancellationToken ct = default);
}
