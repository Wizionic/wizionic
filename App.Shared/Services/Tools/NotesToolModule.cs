using System.ComponentModel;
using System.Text;
using App.Core.Storage;
using App.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ChatMessage = App.Core.Storage.ChatMessage;

namespace App.Shared.Services.Tools;

/// <summary>
/// Native notes tools: list/create notebooks, add/append entries (text + optional chat images).
/// Resolves <see cref="INoteStore"/> at call time (same DI pattern as Gallery/Calendar tools).
/// </summary>
public sealed class NotesToolModule : IToolModule
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationMediaBuffer _media;
    private readonly IToolConversationContext _convoCtx;
    private readonly IToolExecutionTrace _trace;

    public NotesToolModule(
        IServiceProvider services,
        IServiceScopeFactory scopeFactory,
        IConversationMediaBuffer media,
        IToolConversationContext convoCtx,
        IToolExecutionTrace trace)
    {
        _services = services;
        _scopeFactory = scopeFactory;
        _media = media;
        _convoCtx = convoCtx;
        _trace = trace;
    }

    public string ModuleName => "Notes";
    public bool IsAvailable => true;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(ListNotebooksAsync,
            new AIFunctionFactoryOptions
            {
                Name = "list_notebooks",
                Description =
                    "List the user's notebooks (id and title). " +
                    "Use before add_note_entry when the user names a notebook."
            }),
        AIFunctionFactory.Create(ListNoteEntriesAsync,
            new AIFunctionFactoryOptions
            {
                Name = "list_note_entries",
                Description =
                    "List entries in a notebook (entry_id, preview, attachment count). " +
                    "Use before append_to_note_entry to pick the right entry_id."
            }),
        AIFunctionFactory.Create(CreateNotebookAsync,
            new AIFunctionFactoryOptions
            {
                Name = "create_notebook",
                Description =
                    "Create a new empty notebook with the given title. " +
                    "Returns notebook_id. Then call add_note_entry to add content."
            }),
        AIFunctionFactory.Create(AddNoteEntryAsync,
            new AIFunctionFactoryOptions
            {
                Name = "add_note_entry",
                Description =
                    "Add a new entry to a notebook. " +
                    "notebook_name is fuzzy-matched; creates the notebook if create_if_missing is true (default). " +
                    "text is stored as a note entry (HTML-friendly). " +
                    "Optional generation_id attaches a recent chat-generated image to the entry " +
                    "(same buffer as save_to_gallery). Omit generation_id to attach the latest chat image when include_latest_image is true."
            }),
        AIFunctionFactory.Create(AppendToNoteEntryAsync,
            new AIFunctionFactoryOptions
            {
                Name = "append_to_note_entry",
                Description =
                    "Append text and/or an image to an existing note entry (entry_id from list_note_entries). " +
                    "Text is appended as a new paragraph. Images are added as attachments."
            })
    ];

    private NotesWorkScope OpenScope()
    {
        try
        {
            var store = _services.GetService<INoteStore>();
            if (store != null)
            {
                return new NotesWorkScope(
                    store,
                    _services.GetService<INotesSyncBridge>(),
                    owned: null);
            }
        }
        catch
        {
            // Singleton → scoped
        }

        var scope = _scopeFactory.CreateScope();
        return new NotesWorkScope(
            scope.ServiceProvider.GetRequiredService<INoteStore>(),
            scope.ServiceProvider.GetService<INotesSyncBridge>(),
            scope);
    }

    private sealed class NotesWorkScope : IDisposable
    {
        public INoteStore Store { get; }
        public INotesSyncBridge? Sync { get; }
        private readonly IServiceScope? _owned;

        public NotesWorkScope(INoteStore store, INotesSyncBridge? sync, IServiceScope? owned)
        {
            Store = store;
            Sync = sync;
            _owned = owned;
        }

        public void Dispose() => _owned?.Dispose();
    }

    [Description("List notebooks.")]
    private async Task<string> ListNotebooksAsync()
    {
        _trace.Record("📝 list_notebooks()");
        try
        {
            using var work = OpenScope();
            var notes = await work.Store.LoadIndexAsync();
            if (notes.Count == 0)
                return "No notebooks yet. create_notebook or add_note_entry (with create_if_missing) will create one.";

            var sb = new StringBuilder();
            sb.AppendLine("Notebooks:");
            foreach (var n in notes.OrderBy(x => x.SortOrder).ThenByDescending(x => x.LastUpdated))
            {
                var lockMark = n.IsPasswordProtected ? " [password-protected]" : "";
                sb.AppendLine(
                    $"- notebook_id={n.Id} title=\"{n.Title}\"{lockMark} updated={n.LastUpdated.ToLocalTime():yyyy-MM-dd HH:mm}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "Failed to list notebooks: " + ex.Message;
        }
    }

    [Description("List entries in a notebook.")]
    private async Task<string> ListNoteEntriesAsync(
        [Description("Notebook id or title fragment.")] string notebook,
        [Description("Max entries to return (1-40). Default 20.")] int max = 20)
    {
        max = Math.Clamp(max, 1, 40);
        _trace.Record($"📝 list_note_entries(notebook=\"{notebook}\", max={max})");
        try
        {
            using var work = OpenScope();
            var notes = await work.Store.LoadIndexAsync();
            var note = ResolveNotebook(notes, notebook);
            if (note is null)
                return $"No notebook matched \"{notebook}\". Call list_notebooks.";

            if (note.IsPasswordProtected)
                return $"Notebook \"{note.Title}\" is password-protected and cannot be read via tools while locked.";

            var entries = await work.Store.LoadNoteAsync(note.Id);
            var visible = entries.Where(ChatMessageHelper.IsVisible).ToList();
            if (visible.Count == 0)
                return $"Notebook \"{note.Title}\" (id={note.Id}) has no entries yet.";

            var sb = new StringBuilder();
            sb.AppendLine($"Entries in \"{note.Title}\" (notebook_id={note.Id}), newest last:");
            var take = visible.TakeLast(max).ToList();
            var startIndex = Math.Max(0, visible.Count - take.Count);
            for (var i = 0; i < take.Count; i++)
            {
                var e = take[i];
                var preview = PreviewText(e.Content, 120);
                var att = e.Attachments?.Count ?? 0;
                var attMark = att > 0 ? $" attachments={att}" : "";
                sb.AppendLine(
                    $"- entry_id={e.ItemId} index={startIndex + i}{attMark} preview=\"{preview}\"");
            }
            if (visible.Count > max)
                sb.AppendLine($"… {visible.Count - max} older entr(y/ies) omitted.");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "Failed to list note entries: " + ex.Message;
        }
    }

    [Description("Create a new notebook.")]
    private async Task<string> CreateNotebookAsync(
        [Description("Title for the new notebook.")] string title,
        [Description("Optional first entry text.")] string? initial_text = null)
    {
        _trace.Record($"📝 create_notebook(title=\"{title}\")");
        if (string.IsNullOrWhiteSpace(title))
            return "create_notebook failed: title is required.";

        try
        {
            using var work = OpenScope();
            var id = Guid.NewGuid().ToString("N");
            var t = title.Trim();
            var entries = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(initial_text))
            {
                entries.Add(MakeTextEntry(initial_text.Trim()));
            }

            await work.Store.SaveNoteAsync(id, entries);
            await work.Store.UpdateIndexAfterSaveAsync(id, t, entries);
            work.Sync?.ScheduleAutoSyncNoteAfterLocalSave(id, t);

            _trace.Record($"   ✅ notebook_id={id}");
            return entries.Count == 0
                ? $"Created notebook_id={id} title=\"{t}\" (empty). Use add_note_entry to add content."
                : $"Created notebook_id={id} title=\"{t}\" with 1 entry (entry_id={entries[0].ItemId}).";
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "create_notebook failed: " + ex.Message;
        }
    }

    [Description("Add a new entry to a notebook.")]
    private async Task<string> AddNoteEntryAsync(
        [Description("Notebook title fragment or notebook_id.")] string notebook_name,
        [Description("Text body for the new entry (plain text or simple HTML/markdown).")] string? text = null,
        [Description("If true (default), create the notebook when no match is found.")] bool create_if_missing = true,
        [Description("Optional generation_id of a chat-generated image to attach.")] string? generation_id = null,
        [Description("If true and no generation_id, attach the most recent chat image.")] bool include_latest_image = false,
        [Description("Optional image file name when attaching.")] string? image_name = null)
    {
        _trace.Record(
            $"📝 add_note_entry(notebook=\"{notebook_name}\", hasText={!string.IsNullOrWhiteSpace(text)}, " +
            $"gen={generation_id ?? (include_latest_image ? "latest" : "none")})");

        if (string.IsNullOrWhiteSpace(notebook_name))
            return "add_note_entry failed: notebook_name is required.";

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(generation_id) && !include_latest_image)
            return "add_note_entry failed: provide text and/or an image (generation_id or include_latest_image=true).";

        try
        {
            using var work = OpenScope();
            var notes = await work.Store.LoadIndexAsync();
            var note = ResolveNotebook(notes, notebook_name);
            string notebookId;
            string title;

            if (note is null)
            {
                if (!create_if_missing)
                    return $"No notebook matched \"{notebook_name}\". Call list_notebooks or set create_if_missing=true.";

                notebookId = Guid.NewGuid().ToString("N");
                title = notebook_name.Trim();
                await work.Store.SaveNoteAsync(notebookId, new List<ChatMessage>());
                await work.Store.UpdateIndexAfterSaveAsync(notebookId, title);
            }
            else
            {
                if (note.IsPasswordProtected)
                    return $"Notebook \"{note.Title}\" is password-protected; unlock it in the Notes UI first.";
                notebookId = note.Id;
                title = note.Title;
            }

            List<Attachment>? attachments = null;
            string? usedGen = null;
            if (!string.IsNullOrWhiteSpace(generation_id) || include_latest_image)
            {
                if (!TryResolveImage(generation_id, include_latest_image, image_name, out var att, out usedGen, out var err))
                    return "add_note_entry failed: " + err;
                attachments = [att];
            }

            var entry = MakeTextEntry(text ?? "", attachments);
            var entries = await work.Store.LoadNoteAsync(notebookId);
            entries.Add(entry);
            await work.Store.SaveNoteAsync(notebookId, entries);
            await work.Store.UpdateIndexAfterSaveAsync(notebookId, title, entries);
            work.Sync?.ScheduleAutoSyncNoteAfterLocalSave(notebookId, title);

            var imgNote = usedGen is null ? "" : $" image generation_id={usedGen}";
            _trace.Record($"   ✅ entry_id={entry.ItemId} notebook_id={notebookId}");
            return $"Added entry_id={entry.ItemId} to notebook_id={notebookId} title=\"{title}\".{imgNote}";
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "add_note_entry failed: " + ex.Message;
        }
    }

    [Description("Append text and/or an image to an existing entry.")]
    private async Task<string> AppendToNoteEntryAsync(
        [Description("Notebook id or title.")] string notebook,
        [Description("entry_id from list_note_entries.")] string entry_id,
        [Description("Text to append (new paragraph).")] string? text = null,
        [Description("Optional generation_id of a chat image to attach.")] string? generation_id = null,
        [Description("If true and no generation_id, attach the most recent chat image.")] bool include_latest_image = false,
        [Description("Optional image file name when attaching.")] string? image_name = null)
    {
        _trace.Record(
            $"📝 append_to_note_entry(notebook=\"{notebook}\", entry={entry_id}, " +
            $"hasText={!string.IsNullOrWhiteSpace(text)}, gen={generation_id ?? (include_latest_image ? "latest" : "none")})");

        if (string.IsNullOrWhiteSpace(entry_id))
            return "append_to_note_entry failed: entry_id is required.";

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(generation_id) && !include_latest_image)
            return "append_to_note_entry failed: provide text and/or an image.";

        try
        {
            using var work = OpenScope();
            var notes = await work.Store.LoadIndexAsync();
            var note = ResolveNotebook(notes, notebook);
            if (note is null)
                return $"No notebook matched \"{notebook}\".";
            if (note.IsPasswordProtected)
                return $"Notebook \"{note.Title}\" is password-protected.";

            var entries = await work.Store.LoadNoteAsync(note.Id);
            var idx = entries.FindIndex(e =>
                string.Equals(e.ItemId, entry_id, StringComparison.OrdinalIgnoreCase)
                && e.DeletedAt is null);
            if (idx < 0)
                return $"No entry_id={entry_id} in notebook \"{note.Title}\".";

            var existing = entries[idx];
            var content = existing.Content ?? "";
            if (!string.IsNullOrWhiteSpace(text))
            {
                var appendHtml = TextToHtmlParagraphs(text.Trim());
                if (NoteContentFormatter.IsHtml(existing.ContentFormat) || LooksLikeHtml(content))
                {
                    content = (content ?? "").TrimEnd() + appendHtml;
                }
                else if (string.IsNullOrWhiteSpace(content))
                {
                    content = text.Trim();
                }
                else
                {
                    content = content.TrimEnd() + "\n\n" + text.Trim();
                }
            }

            List<Attachment>? attachments = existing.Attachments is { Count: > 0 }
                ? new List<Attachment>(existing.Attachments)
                : null;

            string? usedGen = null;
            if (!string.IsNullOrWhiteSpace(generation_id) || include_latest_image)
            {
                if (!TryResolveImage(generation_id, include_latest_image, image_name, out var att, out usedGen, out var err))
                    return "append_to_note_entry failed: " + err;
                attachments ??= new List<Attachment>();
                attachments.Add(att);
            }

            var format = existing.ContentFormat;
            if (string.IsNullOrWhiteSpace(format) && LooksLikeHtml(content))
                format = NoteContentFormatter.FormatHtml;
            if (!string.IsNullOrWhiteSpace(text) && (NoteContentFormatter.IsHtml(format) || LooksLikeHtml(content)))
                format = NoteContentFormatter.FormatHtml;

            entries[idx] = ChatMessageHelper.TouchModified(
                existing with { Attachments = attachments },
                content: content,
                contentFormat: format);

            await work.Store.SaveNoteAsync(note.Id, entries);
            await work.Store.UpdateIndexAfterSaveAsync(note.Id, note.Title, entries);
            work.Sync?.ScheduleAutoSyncNoteAfterLocalSave(note.Id, note.Title);

            var imgNote = usedGen is null ? "" : $" Attached image generation_id={usedGen}.";
            _trace.Record($"   ✅ appended entry_id={entry_id}");
            return $"Updated entry_id={entry_id} in notebook \"{note.Title}\".{imgNote}";
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "append_to_note_entry failed: " + ex.Message;
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private bool TryResolveImage(
        string? generationId,
        bool includeLatest,
        string? imageName,
        out Attachment attachment,
        out string? usedGenId,
        out string error)
    {
        attachment = null!;
        usedGenId = null;
        error = "";
        var convoId = _convoCtx.ConversationId ?? "_default";

        BufferedImage? img = null;
        if (!string.IsNullOrWhiteSpace(generationId)
            && _media.TryGetImage(convoId, generationId, out var byId) && byId != null)
        {
            img = byId;
        }
        else if (includeLatest || string.IsNullOrWhiteSpace(generationId))
        {
            if (_media.TryGetLatestImage(convoId, out var latest) && latest != null)
                img = latest;
        }

        if (img is null)
        {
            error = "no chat image available. Generate an image first, or pass generation_id / include_latest_image=true.";
            return false;
        }

        var b64 = NormalizeBase64(img.Base64);
        if (string.IsNullOrEmpty(b64))
        {
            error = "image data was empty.";
            return false;
        }

        long size;
        try { size = Convert.FromBase64String(b64).LongLength; }
        catch
        {
            error = "invalid image base64.";
            return false;
        }

        var name = string.IsNullOrWhiteSpace(imageName)
            ? (img.Name ?? "note-image.png")
            : imageName.Trim();
        var ct = string.IsNullOrWhiteSpace(img.ContentType) ? "image/png" : img.ContentType;
        attachment = new Attachment(name, ct, b64, size);
        usedGenId = img.GenerationId;
        return true;
    }

    private static ChatMessage MakeTextEntry(string text, List<Attachment>? attachments = null)
    {
        var html = string.IsNullOrWhiteSpace(text) ? "" : TextToHtmlParagraphs(text);
        // Prefer HTML so Notes Quill display is consistent; empty content ok if image-only
        var format = string.IsNullOrEmpty(html) && attachments is { Count: > 0 }
            ? NoteContentFormatter.FormatHtml
            : (string.IsNullOrEmpty(html) ? null : NoteContentFormatter.FormatHtml);

        if (string.IsNullOrEmpty(html) && attachments is { Count: > 0 })
            html = "<p><br></p>";

        return ChatMessageHelper.Normalize(new ChatMessage(
            Role: "user",
            Content: html,
            Timestamp: DateTime.UtcNow,
            ContentFormat: format,
            Attachments: attachments));
    }

    private static string TextToHtmlParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        // Already HTML
        if (LooksLikeHtml(text))
            return text.Trim();

        var parts = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            parts = [text.Trim()];

        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            var escaped = System.Net.WebUtility.HtmlEncode(p).Replace("\n", "<br>");
            sb.Append("<p>").Append(escaped).Append("</p>");
        }
        return sb.ToString();
    }

    private static bool LooksLikeHtml(string s) =>
        s.Contains('<') && s.Contains('>') &&
        (s.Contains("<p", StringComparison.OrdinalIgnoreCase)
         || s.Contains("<div", StringComparison.OrdinalIgnoreCase)
         || s.Contains("<br", StringComparison.OrdinalIgnoreCase)
         || s.Contains("<ul", StringComparison.OrdinalIgnoreCase)
         || s.Contains("<h", StringComparison.OrdinalIgnoreCase));

    private static LocalNote? ResolveNotebook(List<LocalNote> notes, string nameOrId)
    {
        var q = nameOrId.Trim();
        var byId = notes.FirstOrDefault(n => string.Equals(n.Id, q, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var exact = notes.FirstOrDefault(n => string.Equals(n.Title, q, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return notes.FirstOrDefault(n => n.Title.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private static string PreviewText(string? content, int max)
    {
        if (string.IsNullOrWhiteSpace(content)) return "(empty)";
        var t = content
            .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length <= max) return t;
        return t[..(max - 1)] + "…";
    }

    private static string NormalizeBase64(string input)
    {
        var s = input.Trim();
        var comma = s.IndexOf(',');
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            s = s[(comma + 1)..];
        return s.Replace("\r", "").Replace("\n", "").Trim();
    }
}
