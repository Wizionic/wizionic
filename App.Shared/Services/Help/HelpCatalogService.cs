using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using App.Core.Help;

namespace App.Shared.Services.Help;

/// <summary>
/// Loads shipped help articles from embedded resources. No NavigationManager / HttpClient
/// so this can be a singleton on the host without breaking Development scope validation.
/// </summary>
public sealed class HelpCatalogService : IHelpCatalog
{
    private const string ResourcePrefix = "help.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private bool _loaded;
    private List<HelpTopic> _topics = new();
    private readonly Dictionary<string, string> _markdown = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _searchBlob = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HelpTopic> Topics
    {
        get
        {
            EnsureLoaded();
            lock (_gate)
                return _topics.ToList();
        }
    }

    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        EnsureLoaded();
        return Task.CompletedTask;
    }

    public HelpTopic? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        EnsureLoaded();
        lock (_gate)
            return _topics.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public HelpTopic? FindByRoute(string path)
    {
        EnsureLoaded();
        path = NormalizePath(path);
        if (path.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
            return FindById("start");

        lock (_gate)
        {
            return _topics.FirstOrDefault(t =>
                t.Routes.Any(r => NormalizePath(r) == path && r.Length > 0 && r != "/help"));
        }
    }

    public Task<string> GetMarkdownAsync(string topicId, CancellationToken ct = default)
    {
        EnsureLoaded();
        var topic = FindById(topicId);
        if (topic == null)
            return Task.FromResult("");

        lock (_gate)
        {
            if (_markdown.TryGetValue(topic.File, out var cached))
                return Task.FromResult(cached);
        }

        var raw = ReadResource(topic.File);
        if (raw == null)
            return Task.FromResult($"# {topic.Title}\n\nThis article could not be loaded.");

        var text = StripFrontMatter(raw);
        lock (_gate)
        {
            _markdown[topic.File] = text;
            _searchBlob[topic.Id] = BuildSearchBlob(topic, text);
        }

        return Task.FromResult(text);
    }

    public IReadOnlyList<HelpTopic> Search(string query)
    {
        EnsureLoaded();
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

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded)
                return;

            var json = ReadResource("catalog.json");
            CatalogFile? catalog = null;
            if (!string.IsNullOrWhiteSpace(json))
                catalog = JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions);

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

            _topics = topics;
            foreach (var t in topics)
            {
                _searchBlob[t.Id] = t.Title + " " + t.Id;
                var raw = ReadResource(t.File);
                if (raw == null)
                    continue;
                var text = StripFrontMatter(raw);
                _markdown[t.File] = text;
                _searchBlob[t.Id] = BuildSearchBlob(t, text);
            }

            _loaded = true;
        }
    }

    private List<HelpTopic> VisibleTopics()
    {
        if (AppEnvironment.IsMaui)
            return _topics.ToList();
        return _topics.Where(t => !t.DesktopOnly).ToList();
    }

    private static string? ReadResource(string fileName)
    {
        var asm = typeof(HelpCatalogService).Assembly;
        var name = ResourcePrefix + fileName;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

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
