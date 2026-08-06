using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Browser;

namespace App.Maui.Services;

public sealed class MauiPwaDetector : IPwaDetector
{
    private static readonly Regex ManifestLinkRegex = new(
        """<link[^>]+rel=["'][^"']*\bmanifest\b[^"']*["'][^>]+href=["']([^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ManifestHrefFirstRegex = new(
        """<link[^>]+href=["']([^"']+)["'][^>]+rel=["'][^"']*\bmanifest\b[^"']*["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string FindManifestUrlScript =
        """
        (() => {
            const ready = document.readyState === 'complete' || document.readyState === 'interactive';
            const links = document.querySelectorAll('link[rel]');
            for (const link of links) {
                const rel = (link.getAttribute('rel') || '').toLowerCase();
                if (rel === 'manifest' || rel.split(/\s+/).includes('manifest'))
                    return link.href || null;
            }
            return ready ? null : 'pending';
        })()
        """;

    private const string FetchManifestScript =
        """
        (() => {
            const links = document.querySelectorAll('link[rel]');
            let href = null;
            for (const link of links) {
                const rel = (link.getAttribute('rel') || '').toLowerCase();
                if (rel === 'manifest' || rel.split(/\s+/).includes('manifest')) {
                    href = link.href;
                    break;
                }
            }
            if (!href) return null;
            try {
                const xhr = new XMLHttpRequest();
                xhr.open('GET', href, false);
                xhr.send(null);
                if (xhr.status >= 200 && xhr.status < 300)
                    return xhr.responseText;
            } catch (e) { }
            return null;
        })()
        """;

    private readonly IBrowserAgentService _agent;
    private readonly IBrowserSidebarStore _sidebar;
    private readonly HttpClient _http = new();
    private CancellationTokenSource? _detectCts;

    public MauiPwaDetector(IBrowserAgentService agent, IBrowserSidebarStore sidebar)
    {
        _agent = agent;
        _sidebar = sidebar;
        _agent.UrlChanged += url => _ = ScheduleDetectAsync(url, delayMs: 350);
        _agent.LoadingChanged += loading =>
        {
            if (!loading && !string.IsNullOrWhiteSpace(_agent.CurrentUrl))
                _ = ScheduleDetectAsync(_agent.CurrentUrl, delayMs: 500);
        };
    }

    public PwaManifest? CurrentManifest { get; private set; }

    public bool IsCurrentPagePinned =>
        CurrentManifest != null
        && !string.IsNullOrWhiteSpace(_agent.CurrentUrl)
        && _sidebar.FindPinnedByUrl(PwaManifestHelper.ResolveStartUrl(CurrentManifest, _agent.CurrentUrl)) != null;

    public event Action? Changed;

    public async Task DetectFromPageAsync(CancellationToken ct = default) =>
        await DetectCoreAsync(_agent.CurrentUrl, ct, retryCount: 0);

    public void Clear()
    {
        if (CurrentManifest == null)
            return;

        Console.WriteLine("[Browser/PWA] manifest cleared");
        CurrentManifest = null;
        Changed?.Invoke();
    }

    private async Task ScheduleDetectAsync(string url, int delayMs)
    {
        _detectCts?.Cancel();
        _detectCts = new CancellationTokenSource();
        var token = _detectCts.Token;

        try
        {
            await Task.Delay(delayMs, token);
            await DetectCoreAsync(url, token, retryCount: 2);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DetectCoreAsync(string pageUrl, CancellationToken ct, int retryCount)
    {
        ct.ThrowIfCancellationRequested();

        if (!_agent.IsAvailable || string.IsNullOrWhiteSpace(pageUrl))
        {
            Console.WriteLine($"[Browser/PWA] skip detect — available={_agent.IsAvailable} url='{pageUrl}'");
            Clear();
            return;
        }

        Console.WriteLine($"[Browser/PWA] detecting on {pageUrl} (retries={retryCount})");

        try
        {
            var manifestUrl = await FindManifestUrlAsync(pageUrl, ct);
            Console.WriteLine($"[Browser/PWA] manifest url: '{manifestUrl}'");

            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                if (retryCount > 0)
                {
                    Console.WriteLine($"[Browser/PWA] retrying in 800ms ({retryCount} left)");
                    await Task.Delay(800, ct);
                    await DetectCoreAsync(pageUrl, ct, retryCount - 1);
                    return;
                }

                Console.WriteLine("[Browser/PWA] no manifest link found");
                Clear();
                return;
            }

            var json = await FetchManifestJsonAsync(manifestUrl, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                if (retryCount > 0)
                {
                    Console.WriteLine($"[Browser/PWA] empty manifest, retrying in 800ms ({retryCount} left)");
                    await Task.Delay(800, ct);
                    await DetectCoreAsync(pageUrl, ct, retryCount - 1);
                    return;
                }

                Console.WriteLine("[Browser/PWA] manifest body empty");
                Clear();
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var icons = new List<PwaIcon>();
            if (root.TryGetProperty("icons", out var iconsEl) && iconsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var icon in iconsEl.EnumerateArray())
                {
                    var src = icon.TryGetProperty("src", out var srcEl) ? srcEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(src))
                        continue;

                    icons.Add(new PwaIcon(
                        PwaManifestHelper.ResolveUrl(manifestUrl, src),
                        icon.TryGetProperty("sizes", out var sizesEl) ? sizesEl.GetString() : null,
                        icon.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null));
                }
            }

            var categories = new List<string>();
            if (root.TryGetProperty("categories", out var catEl) && catEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var cat in catEl.EnumerateArray())
                {
                    var value = cat.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        categories.Add(value);
                }
            }

            var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var shortName = root.TryGetProperty("short_name", out var shortEl) ? shortEl.GetString() : null;
            var display = root.TryGetProperty("display", out var displayEl) ? displayEl.GetString() : null;
            var rawStartUrl = root.TryGetProperty("start_url", out var startEl) ? startEl.GetString() : null;
            var resolvedStartUrl = PwaManifestHelper.ResolveOptionalStartUrl(rawStartUrl, manifestUrl);

            CurrentManifest = new PwaManifest(
                name,
                shortName,
                resolvedStartUrl,
                display,
                root.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
                icons,
                categories,
                root.TryGetProperty("background_color", out var bgEl) ? bgEl.GetString() : null,
                root.TryGetProperty("theme_color", out var themeEl) ? themeEl.GetString() : null,
                manifestUrl);

            Console.WriteLine(
                $"[Browser/PWA] detected '{shortName ?? name}' display={display} icons={icons.Count} " +
                $"start_url='{rawStartUrl}' -> '{resolvedStartUrl}'");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser/PWA] detect failed: {ex}");
            Clear();
        }
    }

    private async Task<string?> FindManifestUrlAsync(string pageUrl, CancellationToken ct)
    {
        var manifestUrlRaw = await _agent.EvaluateScriptAsync(FindManifestUrlScript, ct);
        var manifestUrl = UnquoteJsString(manifestUrlRaw);
        Console.WriteLine($"[Browser/PWA] manifest link script: '{manifestUrl}' (raw='{manifestUrlRaw}')");

        if (manifestUrl.Equals("pending", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!string.IsNullOrWhiteSpace(manifestUrl)
            && !manifestUrl.StartsWith("Script error", StringComparison.OrdinalIgnoreCase))
            return manifestUrl;

        var html = UnquoteJsString(await _agent.GetPageHtmlAsync(ct));
        if (!string.IsNullOrWhiteSpace(html)
            && !html.StartsWith("Script error", StringComparison.OrdinalIgnoreCase))
        {
            manifestUrl = ExtractManifestHrefFromHtml(html, pageUrl);
            Console.WriteLine($"[Browser/PWA] manifest link html: '{manifestUrl}'");
            if (!string.IsNullOrWhiteSpace(manifestUrl))
                return manifestUrl;
        }

        manifestUrl = await GuessManifestUrlAsync(pageUrl, ct);
        Console.WriteLine($"[Browser/PWA] manifest link guess: '{manifestUrl}'");
        return manifestUrl;
    }

    private static string? ExtractManifestHrefFromHtml(string html, string pageUrl)
    {
        var match = ManifestLinkRegex.Match(html);
        if (!match.Success)
            match = ManifestHrefFirstRegex.Match(html);

        if (!match.Success)
            return null;

        var href = match.Groups[1].Value.Trim();
        return string.IsNullOrWhiteSpace(href) ? null : PwaManifestHelper.ResolveUrl(pageUrl, href);
    }

    private async Task<string?> GuessManifestUrlAsync(string pageUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
            return null;

        var origin = $"{pageUri.Scheme}://{pageUri.Host}{(pageUri.IsDefaultPort ? "" : $":{pageUri.Port}")}";
        var candidates = new List<string>
        {
            $"{origin}/manifest.webmanifest",
            $"{origin}/manifest.json",
            $"{origin}/site.webmanifest",
            $"{origin}/images/manifest.json"
        };

        if (!string.IsNullOrEmpty(pageUri.AbsolutePath) && pageUri.AbsolutePath != "/")
        {
            var dir = pageUri.AbsolutePath.TrimEnd('/');
            var lastSlash = dir.LastIndexOf('/');
            if (lastSlash >= 0)
                dir = dir[..(lastSlash + 1)];
            else
                dir = "/";

            candidates.Add($"{origin}{dir}manifest.json");
            candidates.Add($"{origin}{dir}manifest.webmanifest");
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var response = await _http.GetAsync(candidate, ct);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Browser/PWA] guess hit {candidate}");
                    return candidate;
                }

                Console.WriteLine($"[Browser/PWA] guess miss {candidate}: HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Browser/PWA] guess miss {candidate}: {ex.Message}");
            }
        }

        return null;
    }

    private async Task<string> FetchManifestJsonAsync(string manifestUrl, CancellationToken ct)
    {
        var pageJson = UnquoteJsString(await _agent.EvaluateScriptAsync(FetchManifestScript, ct));
        if (!string.IsNullOrWhiteSpace(pageJson)
            && !pageJson.StartsWith("Script error", StringComparison.OrdinalIgnoreCase)
            && pageJson.TrimStart().StartsWith('{'))
        {
            Console.WriteLine($"[Browser/PWA] fetched manifest via XHR ({pageJson.Length} chars)");
            return pageJson;
        }

        Console.WriteLine("[Browser/PWA] in-page XHR fetch failed, trying HttpClient");

        try
        {
            var json = await _http.GetStringAsync(manifestUrl, ct);
            Console.WriteLine($"[Browser/PWA] fetched manifest via HttpClient ({json.Length} chars)");
            return json;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser/PWA] HttpClient fetch failed: {ex.Message}");
        }

        return "";
    }

    private static string UnquoteJsString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("undefined", StringComparison.OrdinalIgnoreCase))
            return "";

        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\n", "\n");

        return trimmed;
    }
}