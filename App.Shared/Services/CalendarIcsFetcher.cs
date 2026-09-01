using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Storage;

namespace App.Shared.Services;

/// <summary>Fetches ICS text via the authenticated Home Server proxy (avoids WASM CORS).</summary>
public sealed class CalendarIcsFetcher : ICalendarIcsFetcher
{
    private readonly HttpClient _http;

    public CalendarIcsFetcher(HttpClient http) => _http = http;

    public async Task<CalendarIcsFetchResult> FetchAsync(
        string url,
        string? etag = null,
        string? lastModified = null,
        CancellationToken ct = default)
    {
        var payload = new { Url = url, Etag = etag, LastModified = lastModified };
        using var resp = await _http.PostAsJsonAsync("api/calendar/ics-fetch", payload, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var status = root.TryGetProperty("statusCode", out var sc) ? sc.GetInt32()
                : root.TryGetProperty("StatusCode", out var sc2) ? sc2.GetInt32()
                : (int)resp.StatusCode;
            var text = ReadString(root, "text") ?? ReadString(root, "Text");
            var et = ReadString(root, "etag") ?? ReadString(root, "Etag");
            var lm = ReadString(root, "lastModified") ?? ReadString(root, "LastModified");
            return new CalendarIcsFetchResult(status, text, et, lm);
        }
        catch
        {
            return new CalendarIcsFetchResult((int)resp.StatusCode, null, null, null);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
