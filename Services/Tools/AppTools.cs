using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ChatfishApp.Services.Tools;

/// <summary>
/// App-level tools exposed to models via Microsoft.Extensions.AI function calling.
/// These let capable models (many on OpenRouter, some on other providers) autonomously
/// use web search, page summarization, etc. "when they need to".
///
/// Tools are free / zero-config where possible (DDG for search, Jina Reader for summarization)
/// to align with the app's "completely free models" value.
/// </summary>
public static class AppTools
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Search the web using DuckDuckGo (JSON API first for clean structured data, HTML scrape as fallback).
    /// Returns numbered results with clear Title + URL + Snippet so the model can reliably pick the best ones to follow up with summarize_url.
    /// </summary>
    [Description("Search the web for current, recent, or upcoming/future information (e.g. sports tournaments, events, forecasts, results). Use this when the user asks about recent events, prices, facts, news, weather forecasts, or anything that may have changed or will change. You can find info about future events via search. Strongly prefer official results pages or Omnipong-style tournament sites over social media. Always consider following up the best 1-2 links with summarize_url for full details. Returns top results with titles, links, and short snippets.")]
    public static async Task<string> SearchWeb(
        [Description("The search query, e.g. '2026 Butterfly MAY BTTC Tournament Omnipong winner' or 'latest news on AI regulation'")] string query,
        [Description("Maximum number of results to return (1-8). Default 5.")] int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "No query provided.";

        maxResults = Math.Clamp(maxResults, 1, 8);
        var results = new List<string>();

        // 1. Try the clean JSON API first (much more reliable structure than HTML scraping)
        try
        {
            var jsonUrl = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
            var json = await _http.GetStringAsync(jsonUrl);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Instant answer / abstract
            if (root.TryGetProperty("AbstractText", out var absText) && !string.IsNullOrWhiteSpace(absText.GetString()))
            {
                var absUrl = root.TryGetProperty("AbstractURL", out var u) ? u.GetString() ?? "" : "";
                results.Add($"1. {absText.GetString()}\n   URL: {absUrl}");
            }

            // Direct Results
            if (root.TryGetProperty("Results", out var directResults) && directResults.ValueKind == JsonValueKind.Array)
            {
                int i = results.Count + 1;
                foreach (var r in directResults.EnumerateArray().Take(maxResults))
                {
                    var text = r.TryGetProperty("Text", out var t) ? t.GetString() : "";
                    var url = r.TryGetProperty("FirstURL", out var fu) ? fu.GetString() : "";
                    if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(url))
                    {
                        results.Add($"{i}. {text}\n   URL: {url}");
                        i++;
                    }
                }
            }

            // RelatedTopics (often the best links)
            if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            {
                int i = results.Count + 1;
                foreach (var t in topics.EnumerateArray().Take(Math.Max(0, maxResults - results.Count)))
                {
                    if (t.TryGetProperty("Text", out var txt) && !string.IsNullOrWhiteSpace(txt.GetString()))
                    {
                        var url = t.TryGetProperty("FirstURL", out var fu) ? fu.GetString() : "";
                        results.Add($"{i}. {txt.GetString()}\n   URL: {url}");
                        i++;
                    }
                    else if (t.TryGetProperty("Topics", out var sub) && sub.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var st in sub.EnumerateArray().Take(2))
                        {
                            if (st.TryGetProperty("Text", out var stxt) && !string.IsNullOrWhiteSpace(stxt.GetString()))
                            {
                                var url = st.TryGetProperty("FirstURL", out var fu) ? fu.GetString() : "";
                                results.Add($"{i}. {stxt.GetString()}\n   URL: {url}");
                                i++;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // fall back to HTML
        }

        // 2. Fallback / supplement with the classic HTML scrape if we still have very few good results
        if (results.Count < 2)
        {
            try
            {
                var htmlUrl = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
                var html = await _http.GetStringAsync(htmlUrl);

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'result')]") ?? new HtmlNodeCollection(null);
                int i = results.Count + 1;

                foreach (var node in nodes.Take(maxResults))
                {
                    var a = node.SelectSingleNode(".//a[contains(@class, 'result__a')]");
                    var snippetNode = node.SelectSingleNode(".//a[contains(@class, 'result__snippet')]")
                                   ?? node.SelectSingleNode(".//div[contains(@class, 'result__snippet')]");
                    var snippet = snippetNode?.InnerText?.Trim();

                    if (a != null)
                    {
                        var title = a.InnerText?.Trim() ?? "(no title)";
                        var href = a.GetAttributeValue("href", "");
                        results.Add($"{i}. {title}\n   URL: {href}\n   {snippet}");
                        i++;
                    }
                }
            }
            catch (Exception ex)
            {
                if (results.Count == 0)
                    return $"Web search failed: {ex.Message}. (The model can still answer from its knowledge or try again later.)";
            }
        }

        if (results.Count == 0)
            return "No useful web results found for that query. The information may not be publicly indexed yet.";

        return "Web search results (the model should call summarize_url on the 1-2 most promising official-looking links for full details):\n\n" + string.Join("\n\n", results.Take(maxResults));
    }

    /// <summary>
    /// Fetch and return clean, readable content/summary of any web page using Jina Reader (free tier).
    /// The model can call this on a URL from search results (or any URL the user provides) when it needs the full or summarized page content.
    /// </summary>
    [Description("You have the full ability to fetch and summarize ANY specific website or URL using this tool. Use it to browse specific sites like NOAA, government pages, news articles, etc. when the user asks for content from a particular website. Returns clean text/markdown summary.")]
    public static async Task<string> SummarizeUrl(
        [Description("The full http(s) URL of the page to read/summarize.")] string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return "Invalid or missing URL.";

        try
        {
            // Jina Reader: https://r.jina.ai/ + url  (or https://r.jina.ai/{url})
            var jina = $"https://r.jina.ai/{Uri.EscapeDataString(url)}";
            var content = await _http.GetStringAsync(jina);

            if (string.IsNullOrWhiteSpace(content))
                return "Jina Reader returned empty content for that URL.";

            // Jina returns a nice clean text/markdown representation with some metadata at top.
            return $"Content from {url} (via Jina Reader):\n\n{content}";
        }
        catch (Exception ex)
        {
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
    /// Evaluate a basic arithmetic expression safely.
    /// </summary>
    [Description("Safely evaluate a simple math expression (supports + - * / parentheses and numbers). Use when the user asks to calculate something.")]
    public static string Calculate(
        [Description("The arithmetic expression to evaluate, e.g. '2 + 2 * (3 - 1)' or '15 / 3'")] string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "No expression provided.";

        try
        {
            var table = new System.Data.DataTable();
            var result = table.Compute(expression, string.Empty);
            return $"The result of {expression} is {result}.";
        }
        catch (Exception ex)
        {
            return $"Could not evaluate the expression '{expression}': {ex.Message}. Please provide a valid arithmetic expression.";
        }
    }

    /// <summary>
    /// Get current weather using the free Open-Meteo API (no key required).
    /// </summary>
    [Description("Get current weather or a short-term forecast for a geographic location by latitude and longitude. Supports current conditions and daily forecasts up to 7 days.")]
    public static async Task<string> GetCurrentWeather(
        [Description("Latitude of the location")] double latitude,
        [Description("Longitude of the location")] double longitude,
        [Description("Temperature unit: 'celsius' (default) or 'fahrenheit'")] string units = "celsius",
        [Description("Number of forecast days (0 = current conditions only, 1-7 for daily forecast including tomorrow). Default 0.")] int forecastDays = 0)
    {
        try
        {
            string tempUnit = units.Equals("fahrenheit", StringComparison.OrdinalIgnoreCase) ? "fahrenheit" : "celsius";
            string unitParam = tempUnit == "fahrenheit" ? "fahrenheit" : "celsius";

            string url;
            if (forecastDays > 0)
            {
                url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&daily=temperature_2m_max,temperature_2m_min,weather_code,precipitation_probability_max&forecast_days={Math.Clamp(forecastDays, 1, 7)}&temperature_unit={unitParam}";
            }
            else
            {
                url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&temperature_unit={unitParam}";
            }

            var json = await _http.GetStringAsync(url);

            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (forecastDays > 0)
            {
                var daily = doc.RootElement.GetProperty("daily");
                var times = daily.GetProperty("time").EnumerateArray().Select(x => x.GetString()).ToList();
                var tmax = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.GetDouble()).ToList();
                var tmin = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.GetDouble()).ToList();
                var codes = daily.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).ToList();
                var prec = daily.GetProperty("precipitation_probability_max").EnumerateArray().Select(x => x.GetDouble()).ToList();

                var lines = new List<string>();
                for (int i = 0; i < times.Count; i++)
                {
                    string cond = codes[i] switch
                    {
                        0 => "Clear",
                        1 => "Mainly clear",
                        2 => "Partly cloudy",
                        3 => "Overcast",
                        45 or 48 => "Fog",
                        51 or 53 or 55 => "Drizzle",
                        61 or 63 or 65 => "Rain",
                        71 or 73 or 75 => "Snow",
                        80 or 81 or 82 => "Showers",
                        _ => "Unknown"
                    };
                    lines.Add($"{times[i]}: {tmin[i]:0.0}–{tmax[i]:0.0}°{ (unitParam=="fahrenheit"?"F":"C") }, {cond}, precip chance {prec[i]:0}%");
                }

                return $"Weather forecast for lat {latitude:0.00}, lon {longitude:0.00} ({forecastDays} days):\n" + string.Join("\n", lines);
            }
            else
            {
                var current = doc.RootElement.GetProperty("current");
                double temp = current.GetProperty("temperature_2m").GetDouble();
                double humidity = current.GetProperty("relative_humidity_2m").GetDouble();
                int code = current.GetProperty("weather_code").GetInt32();
                double wind = current.GetProperty("wind_speed_10m").GetDouble();

                string condition = code switch
                {
                    0 => "Clear sky",
                    1 => "Mainly clear",
                    2 => "Partly cloudy",
                    3 => "Overcast",
                    45 or 48 => "Fog",
                    51 or 53 or 55 => "Drizzle",
                    61 or 63 or 65 => "Rain",
                    71 or 73 or 75 => "Snow",
                    80 or 81 or 82 => "Rain showers",
                    _ => "Unknown conditions"
                };

                return $"Current weather at lat {latitude:0.00}, lon {longitude:0.00}: {temp}°{(unitParam == "fahrenheit" ? "F" : "C")}, {condition}, humidity {humidity}%, wind {wind} km/h.";
            }
        }
        catch (Exception ex)
        {
            return $"Failed to retrieve weather for {latitude},{longitude}: {ex.Message}.";
        }
    }
}