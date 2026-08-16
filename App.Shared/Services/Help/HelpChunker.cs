using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using App.Core.Help;

namespace App.Shared.Services.Help;

public static class HelpChunker
{
    private static readonly Regex HeadingLine = new(
        @"^(#{2,3})\s+(.+?)(?:\s+\{#([A-Za-z0-9_-]+)\})?\s*$",
        RegexOptions.Compiled);

    public static async Task<(string Hash, List<HelpChunk> Chunks)> BuildAsync(
        IHelpCatalog catalog,
        CancellationToken ct = default)
    {
        await catalog.EnsureLoadedAsync(ct);
        var hashSource = new StringBuilder();
        var chunks = new List<HelpChunk>();
        var nextId = 1;
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var topic in catalog.Topics)
        {
            ct.ThrowIfCancellationRequested();
            if (!seenFiles.Add(topic.File))
                continue;

            var markdown = await catalog.GetMarkdownAsync(topic.Id, ct);
            hashSource.Append(topic.File).Append('\n').Append(markdown).Append('\n');

            foreach (var piece in SplitFile(topic, markdown))
            {
                chunks.Add(new HelpChunk
                {
                    Id = nextId++,
                    TopicId = piece.TopicId,
                    Title = piece.Title,
                    Anchor = piece.Anchor,
                    Text = piece.Text
                });
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashSource.ToString()))).ToLowerInvariant();
        return (hash, chunks);
    }

    private static List<HelpChunk> SplitFile(HelpTopic topic, string markdown)
    {
        var result = new List<HelpChunk>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var title = topic.Title;
        string? anchor = topic.Anchor;
        var body = new StringBuilder();

        void Flush()
        {
            var text = body.ToString().Trim();
            body.Clear();
            var combined = string.IsNullOrWhiteSpace(title) ? text : title + "\n\n" + text;
            if (string.IsNullOrWhiteSpace(combined))
                return;
            result.Add(new HelpChunk
            {
                TopicId = topic.Id,
                Title = string.IsNullOrWhiteSpace(title) ? topic.Title : title,
                Anchor = anchor,
                Text = combined.Trim()
            });
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal) && !line.StartsWith("##", StringComparison.Ordinal))
                continue;

            var match = HeadingLine.Match(line);
            if (match.Success)
            {
                Flush();
                var heading = match.Groups[2].Value.Trim();
                title = heading;
                anchor = match.Groups[3].Success ? match.Groups[3].Value : Slug(heading);
                continue;
            }

            body.AppendLine(line);
        }

        Flush();
        return result;
    }

    private static string Slug(string heading)
    {
        var s = heading.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[^\p{L}\p{N}\s-]", "");
        s = Regex.Replace(s, @"\s+", "-");
        return s.Trim('-');
    }
}
