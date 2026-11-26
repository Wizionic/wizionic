using System.ComponentModel;
using System.Text;
using App.Core.Storage;
using App.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace App.Shared.Services.Tools;

/// <summary>
/// Native PDF tool: renders markdown to a PDF entirely on the client (PDFsharp),
/// buffers the bytes, and returns a short pdf_id — same pattern as generate_image.
/// The PDF is attached to the assistant message as a downloadable card.
/// Library tools (save/list) resolve IDocumentStore at call time to avoid DI cycles
/// (module is a singleton on MAUI while the store is scoped).
/// </summary>
public sealed class PdfToolModule : IToolModule
{
    private const int MaxMarkdownChars = 200_000;

    private readonly IConversationMediaBuffer _media;
    private readonly IToolConversationContext _convoCtx;
    private readonly IToolExecutionTrace _trace;
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;

    public PdfToolModule(
        IConversationMediaBuffer media,
        IToolConversationContext convoCtx,
        IToolExecutionTrace trace,
        IServiceProvider services,
        IServiceScopeFactory scopeFactory)
    {
        _media = media;
        _convoCtx = convoCtx;
        _trace = trace;
        _services = services;
        _scopeFactory = scopeFactory;
    }

    public string ModuleName => "Pdf";
    public bool IsAvailable => true;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(GeneratePdfAsync,
            new AIFunctionFactoryOptions
            {
                Name = "generate_pdf",
                Description =
                    "Create a PDF document from markdown content. Use when the user asks for a PDF, " +
                    "printable document, report, letter, invoice, summary handout, or 'export as PDF'. " +
                    "Write the complete document body as markdown (headings, paragraphs, bold/italic, " +
                    "lists, code blocks, quotes, links). Tables and images are not rendered. " +
                    "To export a note, first read it with the Notes tools, then pass its content here. " +
                    "Returns a short pdf_id only — the PDF is shown to the user automatically as a " +
                    "preview card. In a project, the file is saved automatically. Outside projects, to keep it call " +
                    "save_pdf_to_library with that pdf_id. Never re-output the document content or base64 in your reply."
            }),
        AIFunctionFactory.Create(SavePdfToLibraryAsync,
            new AIFunctionFactoryOptions
            {
                Name = "save_pdf_to_library",
                Description =
                    "Save a generated PDF into the user's Documents library (persistent, encrypted on this device). " +
                    "Pass pdf_id from generate_pdf, or omit it to save the most recent PDF in this chat. " +
                    "Use when the user says 'save this', 'keep this PDF', 'add to my documents'."
            }),
        AIFunctionFactory.Create(ListLibraryDocuments,
            new AIFunctionFactoryOptions
            {
                Name = "list_library_documents",
                Description =
                    "List documents saved in the user's Documents library (id, name, size, date). " +
                    "Use when the user asks what PDFs/documents they have saved."
            })
    ];

    [Description("Render markdown to a PDF on this device and attach it to the reply.")]
    private Task<string> GeneratePdfAsync(
        [Description("Document title (shown as the main heading and in PDF metadata).")]
        string title,
        [Description("Full document body as markdown.")]
        string markdown,
        [Description("Optional file name, e.g. 'report.pdf'. Defaults to a slug of the title.")]
        string? file_name = null,
        [Description("Page size: 'a4' (default) or 'letter'.")]
        string? page_size = null)
    {
        _trace.Record($"📄 generate_pdf(title=\"{title}\", chars={markdown?.Length ?? 0})");

        if (string.IsNullOrWhiteSpace(markdown))
            return Task.FromResult("generate_pdf failed: markdown content is required.");
        if (markdown.Length > MaxMarkdownChars)
            return Task.FromResult($"generate_pdf failed: content too long ({markdown.Length:N0} chars; max {MaxMarkdownChars:N0}). Split it into smaller documents.");

        try
        {
            var result = MarkdownPdfService.Render(title, markdown, page_size);
            var base64 = Convert.ToBase64String(result.Bytes);
            var name = NormalizeFileName(file_name, title);

            var convoId = _convoCtx.ConversationId ?? "_default";
            var docId = _media.AddDocument(convoId, base64, "application/pdf", name, "native_pdf");

            var sizeKb = result.Bytes.Length / 1024.0;
            _trace.Record($"   ✅ {name} → pdf_id={docId}, {result.PageCount} page(s), {sizeKb:0.#} KB");
            return Task.FromResult(
                $"OK: PDF created. pdf_id={docId}, file \"{name}\", {result.PageCount} page(s), {sizeKb:0.#} KB. " +
                "The PDF can be previewed inline (do not re-output its content or base64). " +
                (ProjectStore.IsChat(convoId) ? "It will be filed with this project." : $"If they asked to keep it, call save_pdf_to_library(pdf_id={docId})."));
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return Task.FromResult("generate_pdf failed: " + ex.Message);
        }
    }

    [Description("Save a generated PDF from this chat into the Documents library.")]
    private async Task<string> SavePdfToLibraryAsync(
        [Description("pdf_id from generate_pdf. Omit to use the most recent PDF in this chat.")]
        string? pdf_id = null,
        [Description("Optional file name override, e.g. 'Q3 report.pdf'.")]
        string? file_name = null)
    {
        _trace.Record($"📚 save_pdf_to_library(pdf={pdf_id ?? "latest"})");

        var convoId = _convoCtx.ConversationId ?? "_default";
        if(ProjectStore.IsChat(convoId))return "Project chat files its generated PDFs automatically when the reply finishes.";
        BufferedDocument? doc = null;
        if (!string.IsNullOrWhiteSpace(pdf_id)
            && _media.TryGetDocument(convoId, pdf_id, out var byId) && byId != null)
        {
            doc = byId;
        }
        else if (_media.ListRecentDocuments(convoId, 1).Count > 0
                 && _media.TryGetDocument(convoId, _media.ListRecentDocuments(convoId, 1)[0].DocumentId, out var latest))
        {
            doc = latest;
        }

        if (doc == null || string.IsNullOrWhiteSpace(doc.Base64))
            return "save_pdf_to_library failed: no PDF available. Generate one with generate_pdf first.";

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(doc.Base64);
        }
        catch
        {
            return "save_pdf_to_library failed: invalid PDF data.";
        }

        var name = string.IsNullOrWhiteSpace(file_name) ? (doc.Name ?? "document.pdf") : file_name.Trim();
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            name += ".pdf";

        try
        {
            var store = ResolveStore();
            var quota = ResolveQuota();
            var (ok, msg) = await DocumentSaveHelper.SaveDocumentAsync(
                store, quota, raw, "application/pdf", name, "chat", convoId);
            _trace.Record(ok ? "   ✅ " + msg : "   ❌ " + msg);
            return ok ? "save_pdf_to_library: " + msg : "save_pdf_to_library failed: " + msg;
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "save_pdf_to_library failed: " + ex.Message;
        }
        finally
        {
            DisposeOwnedScope();
        }
    }

    [Description("List saved documents in the user's Documents library.")]
    private async Task<string> ListLibraryDocuments(
        [Description("Max items to return (1-25). Default 12.")]
        int max = 12)
    {
        max = Math.Clamp(max, 1, 25);
        _trace.Record($"📚 list_library_documents(max={max})");
        try
        {
            var store = ResolveStore();
            var docs = await store.LoadIndexAsync();
            if (docs.Count == 0)
                return "The Documents library is empty. Generate a PDF and save it with save_pdf_to_library.";

            var sb = new StringBuilder();
            sb.AppendLine("Saved documents (newest first):");
            foreach (var d in docs.Take(max))
            {
                var kb = d.Size / 1024.0;
                sb.AppendLine($"- id={d.Id} name=\"{d.Name}\" {kb:0.#} KB {d.CreatedUtc:yyyy-MM-dd}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "list_library_documents failed: " + ex.Message;
        }
        finally
        {
            DisposeOwnedScope();
        }
    }

    // --- scoped resolution (module may be a singleton on MAUI) ---

    private IServiceScope? _ownedScope;

    private IDocumentStore ResolveStore()
    {
        try
        {
            var store = _services.GetService<IDocumentStore>();
            if (store != null)
                return store;
        }
        catch
        {
            // Singleton module resolving scoped store — fall back to a short-lived scope.
        }

        _ownedScope = _scopeFactory.CreateScope();
        return _ownedScope.ServiceProvider.GetRequiredService<IDocumentStore>();
    }

    private IStorageQuotaService? ResolveQuota()
    {
        try
        {
            if (_ownedScope != null)
                return _ownedScope.ServiceProvider.GetService<IStorageQuotaService>();
            return _services.GetService<IStorageQuotaService>();
        }
        catch
        {
            return null;
        }
    }

    private void DisposeOwnedScope()
    {
        _ownedScope?.Dispose();
        _ownedScope = null;
    }

    private static string NormalizeFileName(string? fileName, string? title)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? (title ?? "document") : fileName.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        name = name.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name))
            name = "document";
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            name += ".pdf";
        return name;
    }
}
