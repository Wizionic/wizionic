using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Net.Http.Json;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// App-level tools exposed to models via Microsoft.Extensions.AI function calling (WASM version).
/// 
/// In the WASM client, the actual web calls are proxied through the same-origin server
/// (to avoid CORS issues that the browser would hit with direct calls to DuckDuckGo/Jina).
/// The server performs the real work using its own AppTools implementation and returns the result.
/// 
/// GetCurrentTimeUtc remains fully local.
/// </summary>
public static class AppTools
{
    /// <summary>
    /// The HttpClient used by the WASM tool implementations.
    /// It is configured at startup (in Program.cs) with the correct BaseAddress
    /// so that relative calls like "/api/tools/..." go to the host server.
    /// </summary>
    public static HttpClient HttpClient { get; set; } = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Search the web (proxied via the host server to avoid browser CORS).
    /// </summary>
    [Description("Search the web for current, recent, or upcoming/future information (e.g. sports tournaments, events, forecasts, results). Use this when the user asks about recent events, prices, facts, news, weather forecasts, or anything that may have changed or will change. You can find info about future events via search. Strongly prefer the most relevant-looking official or results-page links over social media. Always consider following up the best 1-2 links with summarize_url for full details. Returns top results with titles, links, and short snippets.")]
    public static async Task<string> SearchWeb(
        [Description("The search query, e.g. 'latest news on AI regulation' or 'current price of Bitcoin'")] string query,
        [Description("Maximum number of results to return (1-10). Default 5.")] int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "No query provided.";

        maxResults = Math.Clamp(maxResults, 1, 10);

        ToolExecutionTrace.Record($"🔎 web_search(query=\"{query}\", maxResults={maxResults})");

        try
        {
            var resp = await HttpClient.PostAsJsonAsync("/api/tools/web-search", new { Query = query, MaxResults = maxResults });
            if (!resp.IsSuccessStatusCode)
            {
                string err = $"Web search proxy failed: {resp.StatusCode}";
                ToolExecutionTrace.Record($"   ❌ {err}");
                return err;
            }

            string result = await resp.Content.ReadAsStringAsync();
            ToolExecutionTrace.Record($"   ✅ returned {result.Length} chars: {result.Substring(0, Math.Min(300, result.Length))}...");
            return result;
        }
        catch (Exception ex)
        {
            ToolExecutionTrace.Record($"   ❌ error: {ex.Message}");
            return $"Web search failed: {ex.Message}. (The model can still answer from its knowledge or try again later.)";
        }
    }

    /// <summary>
    /// Fetch and return clean, readable content/summary of any web page (proxied via the host server).
    /// </summary>
    [Description("You have the full ability to fetch and summarize ANY specific website or URL using this tool. Use it to browse specific sites like NOAA, government pages, news articles, etc. when the user asks for content from a particular website. Returns clean text/markdown summary.")]
    public static async Task<string> SummarizeUrl(
        [Description("The full http(s) URL of the page to read/summarize.")] string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return "Invalid or missing URL.";

        ToolExecutionTrace.Record($"📖 summarize_url(url=\"{url}\")");

        try
        {
            var resp = await HttpClient.PostAsJsonAsync("/api/tools/summarize-url", new { Url = url });
            if (!resp.IsSuccessStatusCode)
            {
                string err = $"Summarize proxy failed: {resp.StatusCode}";
                ToolExecutionTrace.Record($"   ❌ {err}");
                return err;
            }

            string result = await resp.Content.ReadAsStringAsync();
            ToolExecutionTrace.Record($"   ✅ returned {result.Length} chars: {result.Substring(0, Math.Min(300, result.Length))}...");
            return result;
        }
        catch (Exception ex)
        {
            ToolExecutionTrace.Record($"   ❌ error: {ex.Message}");
            return $"Failed to summarize {url}: {ex.Message}. (You can still reason without it or the user can provide the content.)";
        }
    }

    /// <summary>
    /// Simple always-available tool for the current UTC time.
    /// Useful as a trivial demo that the model can call tools, and for any time-sensitive reasoning.
    /// </summary>
    [Description("Get the current date and time in UTC. Use when the user or task needs to know the present moment (e.g. 'what day is it?', relative time calculations, or freshness of information).")]
    public static string GetCurrentTimeUtc()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    }

    /// <summary>
    /// Evaluate a basic arithmetic expression safely. Good for calculations the model shouldn't guess.
    /// </summary>
    [Description("Safely evaluate a simple math expression (supports + - * / parentheses and numbers). Use when the user asks to calculate something.")]
    public static string Calculate(
        [Description("The arithmetic expression to evaluate, e.g. '2 + 2 * (3 - 1)' or '15 / 3'")] string expression)
    {
        ToolExecutionTrace.Record($"🧮 calculate(expression=\"{expression}\")");

        if (string.IsNullOrWhiteSpace(expression))
            return "No expression provided.";

        try
        {
            var table = new System.Data.DataTable();
            var result = table.Compute(expression, string.Empty);
            string formatted = $"The result of {expression} is {result}.";
            ToolExecutionTrace.Record($"   ✅ {formatted}");
            return formatted;
        }
        catch (Exception ex)
        {
            string err = $"Could not evaluate the expression '{expression}': {ex.Message}. Please provide a valid arithmetic expression.";
            ToolExecutionTrace.Record($"   ❌ {err}");
            return err;
        }
    }

    /// <summary>
    /// Get current weather or a short-term forecast using the free Open-Meteo API (no key required). Provide approximate lat/long for the place.
    /// </summary>
    [Description("Get real-time current weather or a forecast via the free Open-Meteo API. You HAVE live weather access when you call this tool — use it for weather questions instead of saying you cannot check. Provide latitude/longitude (approximate coords are fine for named cities, e.g. Santa Cruz, CA ≈ 36.97, -122.03; or SearchWeb first to find coordinates if unsure). Supports current conditions and daily forecasts up to 7 days.")]
    public static async Task<string> GetCurrentWeather(
        [Description("Latitude, e.g. 36.97 for Santa Cruz, CA")] double latitude,
        [Description("Longitude, e.g. -122.03 for Santa Cruz, CA")] double longitude,
        [Description("Temperature unit: 'celsius' (default) or 'fahrenheit'")] string units = "celsius",
        [Description("Number of forecast days (0 = current only, 1-7 for daily forecast including tomorrow). Default 0.")] int forecastDays = 0)
    {
        ToolExecutionTrace.Record($"🌤️ get_current_weather(lat={latitude}, lon={longitude}, units={units}, forecastDays={forecastDays})");

        try
        {
            var resp = await HttpClient.PostAsJsonAsync("/api/tools/get-current-weather", new { Latitude = latitude, Longitude = longitude, Units = units, ForecastDays = forecastDays });
            if (!resp.IsSuccessStatusCode)
            {
                string err = $"Weather proxy failed: {resp.StatusCode}";
                ToolExecutionTrace.Record($"   ❌ {err}");
                return err;
            }

            string result = await resp.Content.ReadAsStringAsync();
            ToolExecutionTrace.Record($"   ✅ returned weather info: {result.Substring(0, Math.Min(300, result.Length))}...");
            return result;
        }
        catch (Exception ex)
        {
            string err = $"Failed to get weather for {latitude},{longitude}: {ex.Message}. Try providing accurate latitude and longitude.";
            ToolExecutionTrace.Record($"   ❌ {err}");
            return err;
        }
    }
}
