using System.ComponentModel;
using System.Net.Http.Json;
using App.Core.Tools;
using Microsoft.Extensions.AI;

namespace App.Shared.Services.Tools;

/// <summary>
/// Native app tools (web search, weather, calculator, etc.) proxied via the host server.
/// </summary>
public sealed class NativeToolModule : IToolModule
{
    private readonly HttpClient _http;
    private readonly IToolExecutionTrace _trace;
    private readonly IReadOnlyList<AITool> _tools;

    public string ModuleName => "Native";
    public bool IsAvailable => true;

    public NativeToolModule(HttpClient http, IToolExecutionTrace trace)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));

        _tools =
        [
            AIFunctionFactory.Create(SearchWeb),
            AIFunctionFactory.Create(SummarizeUrl),
            AIFunctionFactory.Create(GetCurrentTimeUtc),
            AIFunctionFactory.Create(Calculate),
            AIFunctionFactory.Create(GetCurrentWeather)
        ];
    }

    public IReadOnlyList<AITool> GetTools() => _tools;

    [Description("Search the web for current, recent, or upcoming/future information (e.g. sports tournaments, events, forecasts, results). Use this when the user asks about recent events, prices, facts, news, weather forecasts, or anything that may have changed or will change. You can find info about future events via search. Strongly prefer the most relevant-looking official or results-page links over social media. Always consider following up the best 1-2 links with summarize_url for full details. Returns top results with titles, links, and short snippets.")]
    private async Task<string> SearchWeb(
        [Description("The search query, e.g. 'latest news on AI regulation' or 'current price of Bitcoin'")] string query,
        [Description("Maximum number of results to return (1-10). Default 5.")] int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "No query provided.";

        maxResults = Math.Clamp(maxResults, 1, 10);
        _trace.Record($"🔎 web_search(query=\"{query}\", maxResults={maxResults})");

        try
        {
            var resp = await _http.PostAsJsonAsync("/api/tools/web-search", new { Query = query, MaxResults = maxResults });
            if (!resp.IsSuccessStatusCode)
            {
                string err = $"Web search proxy failed: {resp.StatusCode}";
                _trace.Record($"   ❌ {err}");
                return err;
            }

            string result = await resp.Content.ReadAsStringAsync();
            _trace.Record($"   ✅ returned {result.Length} chars: {result.Substring(0, Math.Min(300, result.Length))}...");
            return result;
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ error: {ex.Message}");
            return $"Web search failed: {ex.Message}. (The model can still answer from its knowledge or try again later.)";
        }
    }

    [Description("You have the full ability to fetch and summarize ANY specific website or URL using this tool. Use it to browse specific sites like NOAA, government pages, news articles, etc. when the user asks for content from a particular website. Returns clean text/markdown summary.")]
    private async Task<string> SummarizeUrl(
        [Description("The full http(s) URL of the page to read/summarize.")] string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return "Invalid or missing URL.";

        _trace.Record($"📖 summarize_url(url=\"{url}\")");

        try
        {
            var resp = await _http.PostAsJsonAsync("/api/tools/summarize-url", new { Url = url });
            if (!resp.IsSuccessStatusCode)
            {
                string err = $"Summarize proxy failed: {resp.StatusCode}";
                _trace.Record($"   ❌ {err}");
                return err;
            }

            string result = await resp.Content.ReadAsStringAsync();
            _trace.Record($"   ✅ returned {result.Length} chars: {result.Substring(0, Math.Min(300, result.Length))}...");
            return result;
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ error: {ex.Message}");
            return $"Failed to summarize {url}: {ex.Message}. (You can still reason without it or the user can provide the content.)";
        }
    }

    [Description("Get the current date and time in UTC. Use when the user or task needs to know the present moment (e.g. 'what day is it?', relative time calculations, or freshness of information).")]
    private string GetCurrentTimeUtc() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    [Description("Safely evaluate a simple math expression (supports + - * / parentheses and numbers). Use when the user asks to calculate something.")]
    private string Calculate(
        [Description("The arithmetic expression to evaluate, e.g. '2 + 2 * (3 - 1)' or '15 / 3'")] string expression)
    {
        _trace.Record($"🧮 calculate(expression=\"{expression}\")");

        if (string.IsNullOrWhiteSpace(expression))
            return "No expression provided.";

        try
        {
            var table = new System.Data.DataTable();
            var result = table.Compute(expression, string.Empty);
            string formatted = $"The result of {expression} is {result}.";
            _trace.Record($"   ✅ {formatted}");
            return formatted;
        }
        catch (Exception ex)
        {
            string err = $"Could not evaluate the expression '{expression}': {ex.Message}. Please provide a valid arithmetic expression.";
            _trace.Record($"   ❌ {err}");
            return err;
        }
    }

    [Description("Get real-time current weather or a forecast via the free Open-Meteo API. You HAVE live weather access when you call this tool — use it for weather questions instead of saying you cannot check. Provide latitude/longitude (approximate coords are fine for named cities, e.g. Santa Cruz, CA ≈ 36.97, -122.03; or SearchWeb first to find coordinates if unsure). Supports current conditions and daily forecasts up to 7 days.")]
    private async Task<string> GetCurrentWeather(
        [Description("Latitude, e.g. 36.97 for Santa Cruz, CA")] double latitude,
        [Description("Longitude, e.g. -122.03 for Santa Cruz, CA")] double longitude,
        [Description("Temperature unit: 'celsius' (default) or 'fahrenheit'")] string units = "celsius",
        [Description("Number of forecast days (0 = current only, 1-7 for daily forecast including tomorrow). Default 0.")] int forecastDays = 0)
    {
        _trace.Record($"🌤️ get_current_weather(lat={latitude}, lon={longitude}, units={units}, forecastDays={forecastDays})");

        try
        {
            var resp = await _http.PostAsJsonAsync("/api/tools/get-current-weather",
                new { Latitude = latitude, Longitude = longitude, Units = units, ForecastDays = forecastDays });
            if (!resp.IsSuccessStatusCode)
            {
                string err = $"Weather proxy failed: {resp.StatusCode}";
                _trace.Record($"   ❌ {err}");
                return err;
            }

            string result = await resp.Content.ReadAsStringAsync();
            _trace.Record($"   ✅ returned weather info: {result.Substring(0, Math.Min(300, result.Length))}...");
            return result;
        }
        catch (Exception ex)
        {
            string err = $"Failed to get weather for {latitude},{longitude}: {ex.Message}. Try providing accurate latitude and longitude.";
            _trace.Record($"   ❌ {err}");
            return err;
        }
    }
}