using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using App.Core.Help;
using Microsoft.AspNetCore.Components;

namespace App.Shared.Services.Help;

public sealed class HelpCatalogService : IHelpCatalog
{
    public const string ContentPrefix = "_content/App.Shared/help/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly NavigationManager _nav;
    private readonly object _gate = new();
    private Task? _loadTask;
    private List<HelpTopic> _topics = new();
    private readonly Dictionary<string, string> _markdown = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _searchBlob = new(StringComparer.OrdinalIgnoreCase);

    public HelpCatalogService(NavigationManager nav)
    {
        _nav = nav;
    }

    public IReadOnlyList<HelpTopic> Topics
    {
        get
        {
            lock (_gate)
                return _topics.ToList();
        }
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        Task load;
        lock (_gate)
        {
            _loadTask ??= LoadCoreAsync();
            load = _loadTask;
        }

        await load.WaitAsync(ct);
    }

    public HelpTopic? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        lock (_gate)
            return _topics.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public HelpTopic? FindByRoute(string path)
    {
        path = NormalizePath(path);
        if (path.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
            return FindById("start");

        lock (_gate)
        {
            return _topics.FirstOrDefault(t =>
                t.Routes.Any(r => NormalizePath(r) == path && r.Length > 0 && r != "/help"));
        }
    }

    public async Task<string> GetMarkdownAsync(string topicId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var topic = FindById(topicId);
        if (topic == null)
            return "";

        lock (_gate)
        {
            if (_markdown.TryGetValue(topic.File, out var cached))
                return cached;
        }

        using var http = CreateClient();
        string text;
        try
        {
            text = await http.GetStringAsync(ContentPrefix + topic.File, ct);
        }
        catch
        {
            return $"# {topic.Title}\n\nThis article could not be loaded.";
        }

        text = StripFrontMatter(text);
        lock (_gate)
        {
            _markdown[topic.File] = text;
            _searchBlob[topic.Id] = BuildSearchBlob(topic, text);
        }

        return text;
    }

    public IReadOnlyList<HelpTopic> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            lock (_gate)
                return VisibleTopics();
        }

        var tokens = Regex.Split(query.Trim().ToLowerInvariant(), @"\s+")
            .Where(t => t.Length > 1)
            .Distinct()
            .ToArray();
        if (tokens.Length == 0)
        {
            lock (_gate)
                return VisibleTopics();
        }

        lock (_gate)
        {
            return VisibleTopics()
                .Select(t =>
                {
                    var blob = _searchBlob.GetValueOrDefault(t.Id) ?? (t.Title + " " + t.Id);
                    var hay = blob.ToLowerInvariant();
                    var score = tokens.Count(tok => hay.Contains(tok));
                    return (t, score);
                })
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.t.Title)
                .Select(x => x.t)
                .ToList();
        }
    }

    private async Task LoadCoreAsync()
    {
        using var http = CreateClient();
        CatalogFile? catalog;
        try
        {
            catalog = await http.GetFromJsonAsync<CatalogFile>(ContentPrefix + "catalog.json", JsonOptions);
        }
        catch
        {
            catalog = null;
        }

        var topics = catalog?.Topics?
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.File))
            .Select(t => new HelpTopic
            {
                Id = t.Id!,
                Title = string.IsNullOrWhiteSpace(t.Title) ? t.Id! : t.Title!,
                File = t.File!,
                Audience = t.Audience ?? "howto",
                Anchor = t.Anchor,
                DesktopOnly = t.DesktopOnly,
                Routes = t.Routes ?? Array.Empty<string>()
            })
            .ToList() ?? new List<HelpTopic>();

        if (!AppEnvironment.IsMaui)
            topics = topics.Where(t => !t.DesktopOnly).ToList();

        lock (_gate)
        {
            _topics = topics;
            foreach (var t in topics)
                _searchBlob[t.Id] = t.Title + " " + t.Id;
        }

        foreach (var t in topics.DistinctBy(x => x.File))
        {
            try
            {
                await GetMarkdownAsync(t.Id);
            }
            catch
            {
                // Search still works on titles.
            }
        }
    }

    private List<HelpTopic> VisibleTopics()
    {
        if (AppEnvironment.IsMaui)
            return _topics.ToList();
        return _topics.Where(t => !t.DesktopOnly).ToList();
    }

    private HttpClient CreateClient() =>
        new() { BaseAddress = new Uri(_nav.BaseUri) };

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        var value = path.Trim();
        var q = value.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
            value = value[..q];
        if (!value.StartsWith('/'))
            value = "/" + value;
        if (value.Length > 1)
            value = value.TrimEnd('/');
        return value;
    }

    private static string StripFrontMatter(string markdown)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal))
            return markdown;
        var end = markdown.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return markdown;
        var body = markdown[(end + 4)..];
        return body.TrimStart('\r', '\n');
    }

    private static string BuildSearchBlob(HelpTopic topic, string markdown)
    {
        var headings = Regex.Matches(markdown, @"^#{1,3}\s+(.+)$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value);
        return string.Join(" ", new[] { topic.Title, topic.Id }.Concat(headings).Append(markdown));
    }

    private sealed class CatalogFile
    {
        public List<CatalogTopic>? Topics { get; set; }
    }

    private sealed class CatalogTopic
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? File { get; set; }
        public string? Audience { get; set; }
        public string? Anchor { get; set; }
        public bool DesktopOnly { get; set; }
        [JsonPropertyName("routes")]
        public string[]? Routes { get; set; }
    }
}
