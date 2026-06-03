using Microsoft.Extensions.AI;
using System.ComponentModel;
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
    /// Search the web (free, using DuckDuckGo HTML results).
    /// Returns a compact list of title + link + snippet for the model to reason over.
    /// The model can then decide to call summarize_url on promising links if it needs full content.
    /// </summary>
    [Description("Search the web for current or factual information on a topic. Use this when the user asks about recent events, prices, facts, news, or anything that may have changed since your last knowledge. Returns top results with titles, links, and short snippets.")]
    public static async Task<string> SearchWeb(
        [Description("The search query, e.g. 'latest news on AI regulation' or 'current price of Bitcoin'")] string query,
        [Description("Maximum number of results to return (1-10). Default 5.")] int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "No query provided.";

        maxResults = Math.Clamp(maxResults, 1, 10);

        try
        {
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            var html = await _http.GetStringAsync(url);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = new List<string>();
            var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'result')]") ?? new HtmlNodeCollection(null);

            foreach (var node in nodes.Take(maxResults))
            {
                var a = node.SelectSingleNode(".//a[contains(@class, 'result__a')]");
                var snippet = node.SelectSingleNode(".//a[contains(@class, 'result__snippet')]")?.InnerText?.Trim()
                              ?? node.SelectSingleNode(".//div[contains(@class, 'result__snippet')]")?.InnerText?.Trim();

                if (a != null)
                {
                    var title = a.InnerText?.Trim() ?? "(no title)";
                    var href = a.GetAttributeValue("href", "");
                    // DDG sometimes uses a redirect; the real url is often in the href or we leave it.
                    results.Add($"- {title}\n  {href}\n  {snippet}");
                }
            }

            if (results.Count == 0)
                return "No web results found (or the search backend returned an unexpected page).";

            return "Web search results:\n" + string.Join("\n\n", results);
        }
        catch (Exception ex)
        {
            return $"Web search failed: {ex.Message}. (The model can still answer from its knowledge or try again later.)";
        }
    }

    /// <summary>
    /// Fetch and return clean, readable content/summary of any web page using Jina Reader (free tier).
    /// The model can call this on a URL from search results (or any URL the user provides) when it needs the full or summarized page content.
    /// </summary>
    [Description("Fetch and summarize the main content of a web page as clean text (or markdown). Use this when you have a specific URL and need its full details, article body, or a concise summary instead of just the search snippet.")]
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
}