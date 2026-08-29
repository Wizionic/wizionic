using System.Text.RegularExpressions;
using App.Core.Storage;
using App.Shared.Services.Help;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using NoteChatMessage = App.Core.Storage.ChatMessage;

namespace App.Shared.Services;

public sealed class NotesSearchService : INotesSearchService
{
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);
    private const int TopK = 8;
    private const string SystemPrompt =
        "You answer questions using only the user's note excerpts below. " +
        "Cite the notebook title in plain language. If the excerpts are not enough, say so. " +
        "Do not invent notebooks or quotes.";

    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKeyStore _keys;
    private readonly ChatModelCatalogService _models;
    private readonly HelpEmbeddingClient _embed;

    public NotesSearchService(
        IServiceProvider services,
        IServiceScopeFactory scopeFactory,
        IKeyStore keys,
        ChatModelCatalogService models,
        HelpEmbeddingClient embed)
    {
        _services = services;
        _scopeFactory = scopeFactory;
        _keys = keys;
        _models = models;
        _embed = embed;
    }

    public async Task<IReadOnlyList<NotesSearchHit>> SearchAsync(
        string query,
        int max = 8,
        IReadOnlySet<string>? unlockedNotebookIds = null,
        CancellationToken ct = default)
    {
        max = Math.Clamp(max, 1, 40);
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return Array.Empty<NotesSearchHit>();

        using var work = OpenStore();
        if (work.Store == null)
            return Array.Empty<NotesSearchHit>();

        var tokens = Tokenize(q);
        var notes = await work.Store.LoadIndexAsync(ct);
        var hits = new List<NotesSearchHit>();

        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            if (note.IsPasswordProtected
                && (unlockedNotebookIds == null || !unlockedNotebookIds.Contains(note.Id)))
                continue;

            List<NoteChatMessage> entries;
            try { entries = await work.Store.LoadNoteAsync(note.Id, ct); }
            catch { continue; }

            var titleHay = (note.Title ?? "").ToLowerInvariant();
            var titleScore = tokens.Length == 0 ? 0 : tokens.Count(t => titleHay.Contains(t));

            foreach (var entry in entries.Where(ChatMessageHelper.IsVisible))
            {
                var plain = ToPlain(entry.Content);
                var hay = (note.Title + " " + plain).ToLowerInvariant();
                var score = tokens.Length == 0 ? 0f : tokens.Count(t => hay.Contains(t));
                if (titleScore > 0)
                    score += titleScore * 0.5f;
                if (score <= 0 && tokens.Length > 0)
                    continue;
                if (tokens.Length == 0 && titleScore <= 0)
                    continue;

                hits.Add(new NotesSearchHit(
                    note.Id,
                    note.Title ?? "Untitled",
                    entry.ItemId ?? "",
                    Snippet(plain, q, 160),
                    score));
            }
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.NotebookTitle, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    public async Task<NotesAskResult> AskAsync(
        string question,
        IReadOnlySet<string>? unlockedNotebookIds = null,
        CancellationToken ct = default)
    {
        var q = (question ?? "").Trim();
        if (q.Length == 0)
            return new NotesAskResult { Error = "Type a question first." };

        var model = ResolveAnswerModel();
        if (string.IsNullOrWhiteSpace(model))
            return new NotesAskResult { Error = "Pick a chat model first, then ask from Notes." };

        var hits = await SearchAsync(q, TopK, unlockedNotebookIds, ct);
        if (hits.Count == 0)
        {
            return new NotesAskResult
            {
                AnswerMarkdown = "I could not find matching notes. Try a shorter phrase, or unlock a protected notebook.",
                Citations = hits
            };
        }

        try
        {
            var context = string.Join("\n\n---\n\n", hits.Select((h, i) =>
                $"[{i + 1}] {h.NotebookTitle}\n{h.Snippet}"));
            var system = SystemPrompt + "\n\nExcerpts:\n" + context;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));

            string text;
            if (_embed.SupportsDirectChat(model))
            {
                text = await _embed.CompleteAsync(model, system, q, timeout.Token);
            }
            else
            {
                var client = _models.GetChatClientForModel(model);
                var history = new List<AiChatMessage>
                {
                    new(ChatRole.System, system),
                    new(ChatRole.User, q)
                };
                var response = await client.GetResponseAsync(
                    history,
                    new ChatOptions { MaxOutputTokens = 2048 },
                    timeout.Token);
                text = response.Text ?? "";
            }

            if (string.IsNullOrWhiteSpace(text))
                text = "The model returned an empty answer. Open a cited notebook from search.";

            return new NotesAskResult { AnswerMarkdown = text.Trim(), Citations = hits };
        }
        catch (Exception ex)
        {
            return new NotesAskResult { Error = ex.Message, Citations = hits };
        }
    }

    private StoreScope OpenStore()
    {
        try
        {
            var store = _services.GetService<INoteStore>();
            if (store != null)
                return new StoreScope(store, null);
        }
        catch
        {
            // Singleton → scoped
        }

        var scope = _scopeFactory.CreateScope();
        return new StoreScope(scope.ServiceProvider.GetRequiredService<INoteStore>(), scope);
    }

    private sealed class StoreScope : IDisposable
    {
        public INoteStore Store { get; }
        private readonly IServiceScope? _owned;
        public StoreScope(INoteStore store, IServiceScope? owned)
        {
            Store = store;
            _owned = owned;
        }
        public void Dispose() => _owned?.Dispose();
    }

    private string? ResolveAnswerModel()
    {
        var last = _keys.LastSelectedModel;
        if (ModelProfileId.TryParsePicker(last, out var pid))
            return _keys.GetModelProfile(pid)?.ChatModelId;
        if (!string.IsNullOrWhiteSpace(last) && !last.Contains("/image", StringComparison.OrdinalIgnoreCase))
            return last;
        return _keys.HelpAnswerModelId;
    }

    private static string[] Tokenize(string question) =>
        question.ToLowerInvariant()
            .Split([' ', '\t', '\n', ',', '.', '?', '!', ';', ':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Distinct()
            .ToArray();

    private static string ToPlain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var t = html.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</div>", " ", StringComparison.OrdinalIgnoreCase);
        t = HtmlTag.Replace(t, " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static string Snippet(string plain, string query, int max)
    {
        if (string.IsNullOrEmpty(plain))
            return "";
        var hay = plain.ToLowerInvariant();
        var needle = query.Trim().ToLowerInvariant();
        var idx = hay.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0)
        {
            var first = Tokenize(query).FirstOrDefault();
            if (first != null)
                idx = hay.IndexOf(first, StringComparison.Ordinal);
        }
        if (idx < 0)
            idx = 0;
        var start = Math.Max(0, idx - 24);
        var len = Math.Min(max, plain.Length - start);
        var slice = plain.Substring(start, len);
        if (start > 0) slice = "…" + slice;
        if (start + len < plain.Length) slice += "…";
        return slice;
    }
}
