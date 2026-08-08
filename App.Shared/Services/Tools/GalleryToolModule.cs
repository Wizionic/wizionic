using System.ComponentModel;
using System.Text;
using App.Core.Storage;
using App.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace App.Shared.Services.Tools;

/// <summary>
/// Native gallery tools: list albums, list recent chat images, save image to album.
/// Resolves <see cref="IGalleryStore"/> / sync at call time to avoid DI cycles
/// (GalleryToolModule → IGallerySyncBridge → SyncService → ChatCompletion → tools).
/// Prefers the ambient (circuit) <see cref="IServiceProvider"/> so IJSRuntime thumb
/// generation works; falls back to a new scope when tools are registered as singletons.
/// </summary>
public sealed class GalleryToolModule : IToolModule
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationMediaBuffer _media;
    private readonly IToolConversationContext _convoCtx;
    private readonly IToolExecutionTrace _trace;

    public GalleryToolModule(
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

    /// <summary>
    /// Prefer ambient scoped services (WASM circuit / same Blazor scope).
    /// When this module is a singleton (MAUI), fall back to a short-lived scope.
    /// </summary>
    private GalleryWorkScope OpenGalleryScope()
    {
        try
        {
            var gallery = _services.GetService<IGalleryStore>();
            if (gallery != null)
            {
                return new GalleryWorkScope(
                    gallery,
                    _services.GetService<IGallerySyncBridge>(),
                    _services.GetService<IStorageQuotaService>(),
                    ownedScope: null);
            }
        }
        catch
        {
            // Singleton module resolving scoped store — use factory below.
        }

        var scope = _scopeFactory.CreateScope();
        return new GalleryWorkScope(
            scope.ServiceProvider.GetRequiredService<IGalleryStore>(),
            scope.ServiceProvider.GetService<IGallerySyncBridge>(),
            scope.ServiceProvider.GetService<IStorageQuotaService>(),
            scope);
    }

    private sealed class GalleryWorkScope : IDisposable
    {
        public IGalleryStore Gallery { get; }
        public IGallerySyncBridge? SyncBridge { get; }
        public IStorageQuotaService? Quota { get; }
        private readonly IServiceScope? _owned;

        public GalleryWorkScope(
            IGalleryStore gallery,
            IGallerySyncBridge? syncBridge,
            IStorageQuotaService? quota,
            IServiceScope? ownedScope)
        {
            Gallery = gallery;
            SyncBridge = syncBridge;
            Quota = quota;
            _owned = ownedScope;
        }

        public void Dispose() => _owned?.Dispose();
    }

    public string ModuleName => "Gallery";
    public bool IsAvailable => true;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(ListGalleryAlbumsAsync,
            new AIFunctionFactoryOptions
            {
                Name = "list_gallery_albums",
                Description =
                    "List the user's photo gallery albums (id and title). " +
                    "Use before save_to_gallery when you need exact album names."
            }),
        AIFunctionFactory.Create(ListRecentChatImages,
            new AIFunctionFactoryOptions
            {
                Name = "list_recent_chat_images",
                Description =
                    "List recently generated/edited images in this chat (generation_id, name, source, size). " +
                    "Does not include image bytes. Use to pick the right generation_id when several images exist " +
                    "before calling save_to_gallery."
            }),
        AIFunctionFactory.Create(SaveToGalleryAsync,
            new AIFunctionFactoryOptions
            {
                Name = "save_to_gallery",
                Description =
                    "Save an image into a gallery album. Prefer generation_id from lemonade_generate_image / lemonade_edit_image " +
                    "(or omit it to save the most recent image in this chat). " +
                    "album_name is fuzzy-matched to existing albums; a new album is created only if none match. " +
                    "Do not pass multi-MB image_base64 unless necessary."
            })
    ];

    [Description("List gallery albums available to the user.")]
    private async Task<string> ListGalleryAlbumsAsync()
    {
        _trace.Record("🖼️ list_gallery_albums()");
        try
        {
            using var work = OpenGalleryScope();
            var albums = await work.Gallery.LoadIndexAsync();
            if (albums.Count == 0)
                return "No gallery albums yet. save_to_gallery will create one when given an album_name.";

            var sb = new StringBuilder();
            sb.AppendLine("Gallery albums:");
            foreach (var a in albums.OrderBy(x => x.SortOrder).ThenBy(x => x.Title))
            {
                var lockMark = a.IsPasswordProtected ? " [password-protected]" : "";
                sb.AppendLine($"- id={a.Id} title=\"{a.Title}\"{lockMark}");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "Failed to list albums: " + ex.Message;
        }
    }

    [Description("List recent AI-generated images in this conversation (ids only).")]
    private string ListRecentChatImages(
        [Description("Max items to return (1-8). Default 8.")] int max = 8)
    {
        var convoId = _convoCtx.ConversationId ?? "_default";
        _trace.Record($"🖼️ list_recent_chat_images(convo={convoId}, max={max})");
        var items = _media.ListRecent(convoId, max);
        if (items.Count == 0)
            return "No recent generated images in this chat. Generate or edit an image first.";

        var sb = new StringBuilder();
        sb.AppendLine("Recent chat images (newest first):");
        foreach (var i in items)
        {
            var age = DateTime.UtcNow - i.CreatedUtc;
            var ageStr = age.TotalMinutes < 1 ? $"{age.TotalSeconds:0}s ago"
                : age.TotalHours < 1 ? $"{age.TotalMinutes:0}m ago"
                : $"{age.TotalHours:0}h ago";
            sb.AppendLine(
                $"- generation_id={i.GenerationId} name=\"{i.Name ?? "image"}\" source={i.Source ?? "?"} " +
                $"~{FormatBytes(i.ApproxBytes)} ({ageStr})");
        }

        return sb.ToString().TrimEnd();
    }

    [Description("Save an image to a gallery album by name (fuzzy match or create).")]
    private async Task<string> SaveToGalleryAsync(
        [Description("Album title or fragment, e.g. the name the user said.")] string album_name,
        [Description("Optional generation_id from a prior generate/edit tool result.")] string? generation_id = null,
        [Description("Optional raw base64 image (avoid for large images). Prefer generation_id.")] string? image_base64 = null,
        [Description("Optional file name for the saved image.")] string? image_name = null)
    {
        _trace.Record(
            $"🖼️ save_to_gallery(album=\"{album_name}\", gen={generation_id ?? "latest"}, hasB64={!string.IsNullOrEmpty(image_base64)})");

        if (string.IsNullOrWhiteSpace(album_name))
            return "save_to_gallery failed: album_name is required.";

        var convoId = _convoCtx.ConversationId ?? "_default";
        string base64;
        string contentType;
        string name;
        string? usedGenId = generation_id;

        if (!string.IsNullOrWhiteSpace(image_base64))
        {
            base64 = NormalizeBase64(image_base64);
            contentType = "image/png";
            name = string.IsNullOrWhiteSpace(image_name) ? "saved-image.png" : image_name.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(generation_id)
                 && _media.TryGetImage(convoId, generation_id, out var byId) && byId != null)
        {
            base64 = NormalizeBase64(byId.Base64);
            contentType = byId.ContentType;
            name = string.IsNullOrWhiteSpace(image_name) ? (byId.Name ?? "generated-image.png") : image_name.Trim();
            usedGenId = byId.GenerationId;
        }
        else if (_media.TryGetLatestImage(convoId, out var latest) && latest != null)
        {
            base64 = NormalizeBase64(latest.Base64);
            contentType = latest.ContentType;
            name = string.IsNullOrWhiteSpace(image_name) ? (latest.Name ?? "generated-image.png") : image_name.Trim();
            usedGenId = latest.GenerationId;
        }
        else
        {
            return "save_to_gallery failed: no image available. Generate/edit an image first, " +
                   "or pass generation_id / image_base64.";
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(base64);
        }
        catch
        {
            return "save_to_gallery failed: invalid image base64.";
        }

        if (raw.Length == 0)
            return "save_to_gallery failed: empty image.";

        try
        {
            using var work = OpenGalleryScope();
            var (ok, msg) = await GallerySaveHelper.SaveImageAsync(
                work.Gallery, work.SyncBridge, work.Quota, album_name.Trim(), raw, contentType, name);
            if (!ok)
            {
                _trace.Record("   ❌ " + msg);
                return "save_to_gallery failed: " + msg;
            }

            var withId = string.IsNullOrEmpty(usedGenId) ? msg : msg.TrimEnd('.') + $" (generation_id={usedGenId}).";
            _trace.Record("   ✅ " + withId);
            return withId;
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ {ex.Message}");
            return "save_to_gallery failed: " + ex.Message;
        }
    }

    private static string NormalizeBase64(string imageBase64)
    {
        var b64 = imageBase64.Trim();
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            b64 = b64[(comma + 1)..];
        return b64;
    }

    private static string FormatBytes(long n) =>
        n < 1024 ? $"{n} B"
        : n < 1024 * 1024 ? $"{n / 1024.0:0.#} KB"
        : $"{n / (1024.0 * 1024.0):0.#} MB";
}

