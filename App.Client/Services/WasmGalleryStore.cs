using App.Core.Auth;
using App.Core.Storage;
using Microsoft.JSInterop;
using System.Text.Json;

namespace App.Client.Services;

/// <summary>
/// Per-image gallery storage in IndexedDB (albumMetas + albumImageMetas + albumImageContents).
/// </summary>
public class WasmGalleryStore : IGalleryStore
{
    private const string AlbumPrefix = "a-wasmchat-album-";
    private const string ImagePrefix = "a-wasmchat-img-";

    private readonly IAuthService _auth;
    private readonly ICryptoService _crypto;
    private readonly IJSRuntime _js;

    public WasmGalleryStore(IAuthService auth, ICryptoService crypto, IJSRuntime js)
    {
        _auth = auth;
        _crypto = crypto;
        _js = js;
    }

    private record StoredAlbumMeta(
        string key,
        string id,
        string @namespace,
        string title,
        string lastUpdated,
        bool syncEnabled,
        string? contentFingerprint,
        string? deletedAt,
        bool? isPasswordProtected = null,
        int? sortOrder = null,
        long? protectionChangedTicks = null);

    private record StoredImageMeta(
        string key,
        string id,
        string albumId,
        string @namespace,
        string name,
        string contentType,
        long size,
        int? width,
        int? height,
        string lastUpdated,
        string? contentFingerprint,
        string? deletedAt,
        int? sortOrder = null,
        /// <summary>JPEG thumb base64 for grid — stored on meta so we never decrypt multi-MB blobs for tiles.</summary>
        string? thumbnailBase64 = null);

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private async Task<string> GetKeyAsync() =>
        await _auth.GetOrCreateHistoryEncryptionKeyAsync();

    private string AlbumMetaKey(string ns, string albumId) => ns + AlbumPrefix + albumId;
    private string ImageMetaKey(string ns, string albumId, string imageId) => ns + ImagePrefix + albumId + "-" + imageId;
    private string ImageContentKey(string ns, string albumId, string imageId) => ns + ImagePrefix + "c-" + albumId + "-" + imageId;

    private async Task<StoredAlbumMeta?> GetAlbumMetaAsync(string albumId)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredAlbumMeta>>("idbGetAlbumMetasByNamespace", ns);
        return metas.FirstOrDefault(m => string.Equals(m.id, albumId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<StoredImageMeta>> GetImageMetasForAlbumAsync(string albumId)
    {
        var ns = GetPrefix();
        var all = await _js.InvokeAsync<List<StoredImageMeta>>("idbGetAlbumImageMetasByNamespace", ns);
        return all.Where(m => string.Equals(m.albumId, albumId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static long ImageTicks(GalleryImage img)
    {
        long max = 0;
        if (img.ModifiedAt.HasValue) max = Math.Max(max, img.ModifiedAt.Value.Ticks);
        if (img.DeletedAt.HasValue) max = Math.Max(max, img.DeletedAt.Value.Ticks);
        if (img.Timestamp.HasValue) max = Math.Max(max, img.Timestamp.Value.Ticks);
        return max;
    }

    private static string ImageFp(string albumId, GalleryImage img) =>
        SyncFingerprint.ForAlbumImage(albumId, img);

    private async Task<List<AlbumImageRef>> BuildImageRefsAsync(string albumId, CancellationToken ct)
    {
        var metas = await GetImageMetasForAlbumAsync(albumId);
        var refs = new List<AlbumImageRef>();
        var order = 0;
        foreach (var m in metas.OrderBy(x => x.sortOrder ?? 0).ThenBy(x => x.id))
        {
            long? delTicks = null;
            if (!string.IsNullOrEmpty(m.deletedAt))
                delTicks = DateTime.Parse(m.deletedAt).Ticks;
            var fp = m.contentFingerprint ?? "";
            if (string.IsNullOrEmpty(fp) && string.IsNullOrEmpty(m.deletedAt))
            {
                var img = await LoadImageAsync(albumId, m.id, ct);
                if (img != null)
                    fp = ImageFp(albumId, img);
            }
            refs.Add(new AlbumImageRef(m.id, fp, order++, delTicks));
        }
        return refs;
    }

    private async Task PersistAlbumMetaAsync(
        string albumId,
        string title,
        bool isProtected,
        long proticks,
        int sortOrder,
        string? deletedAt,
        CancellationToken ct)
    {
        var ns = GetPrefix();
        var syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);
        var refs = string.IsNullOrEmpty(deletedAt)
            ? await BuildImageRefsAsync(albumId, ct)
            : new List<AlbumImageRef>();
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "(empty)" : title;
        var fp = string.IsNullOrEmpty(deletedAt)
            ? SyncFingerprint.ForAlbumMeta(albumId, normalizedTitle, refs, isProtected, proticks)
            : DeleteSyncPayload.AckValue(DateTime.Parse(deletedAt).Ticks);

        await _js.InvokeVoidAsync("idbPutAlbumMeta", new
        {
            key = AlbumMetaKey(ns, albumId),
            id = albumId,
            @namespace = ns,
            title = normalizedTitle,
            lastUpdated = DateTime.UtcNow.ToString("o"),
            syncEnabled,
            contentFingerprint = fp,
            deletedAt = deletedAt ?? "",
            isPasswordProtected = isProtected,
            sortOrder,
            protectionChangedTicks = proticks
        });
    }

    public async Task<List<LocalAlbum>> LoadIndexAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredAlbumMeta>>("idbGetAlbumMetasByNamespace", ns);
        return metas
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .OrderBy(m => m.sortOrder ?? 0)
            .ThenByDescending(m => m.lastUpdated)
            .Select(m => new LocalAlbum(
                m.id,
                string.IsNullOrWhiteSpace(m.title) ? "(empty)" : m.title,
                DateTime.Parse(m.lastUpdated),
                m.isPasswordProtected == true,
                m.sortOrder ?? 0,
                m.protectionChangedTicks ?? 0))
            .ToList();
    }

    public async Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredAlbumMeta>>("idbGetAlbumMetasByNamespace", ns);
        var entries = new List<SyncManifestEntry>();
        foreach (var m in metas)
        {
            var title = string.IsNullOrWhiteSpace(m.title) ? "(empty)" : m.title;
            long? deletedAtTicks = null;
            if (!string.IsNullOrEmpty(m.deletedAt))
                deletedAtTicks = DateTime.Parse(m.deletedAt).Ticks;

            var fingerprint = deletedAtTicks.HasValue
                ? DeleteSyncPayload.AckValue(deletedAtTicks.Value)
                : m.contentFingerprint ?? "";

            if (!deletedAtTicks.HasValue && (backfillMissingFingerprints || string.IsNullOrEmpty(fingerprint)))
            {
                var refs = await BuildImageRefsAsync(m.id, ct);
                fingerprint = SyncFingerprint.ForAlbumMeta(
                    m.id, title, refs, m.isPasswordProtected == true, m.protectionChangedTicks ?? 0);
                if (backfillMissingFingerprints)
                {
                    await _js.InvokeVoidAsync("idbPutAlbumMeta", new
                    {
                        key = m.key,
                        id = m.id,
                        @namespace = m.@namespace,
                        title = m.title,
                        lastUpdated = m.lastUpdated,
                        syncEnabled = m.syncEnabled,
                        contentFingerprint = fingerprint,
                        deletedAt = m.deletedAt ?? "",
                        isPasswordProtected = m.isPasswordProtected == true,
                        sortOrder = m.sortOrder ?? 0,
                        protectionChangedTicks = m.protectionChangedTicks ?? 0
                    });
                }
            }

            entries.Add(new SyncManifestEntry(
                m.id, title, DateTime.Parse(m.lastUpdated).Ticks, fingerprint, deletedAtTicks));
        }
        return entries;
    }

    public async Task<List<SyncManifestEntry>> LoadImageManifestEntriesAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredImageMeta>>("idbGetAlbumImageMetasByNamespace", ns);
        var entries = new List<SyncManifestEntry>();
        foreach (var m in metas)
        {
            long? deletedAtTicks = null;
            if (!string.IsNullOrEmpty(m.deletedAt))
                deletedAtTicks = DateTime.Parse(m.deletedAt).Ticks;

            var composite = GalleryImageSyncPayload.CompositeId(m.albumId, m.id);
            var fingerprint = deletedAtTicks.HasValue
                ? DeleteSyncPayload.AckValue(deletedAtTicks.Value)
                : m.contentFingerprint ?? "";

            if (!deletedAtTicks.HasValue && string.IsNullOrEmpty(fingerprint))
            {
                var img = await LoadImageAsync(m.albumId, m.id, ct);
                if (img != null)
                    fingerprint = ImageFp(m.albumId, img);
            }

            entries.Add(new SyncManifestEntry(
                composite,
                m.name,
                DateTime.Parse(m.lastUpdated).Ticks,
                fingerprint,
                deletedAtTicks));
        }
        return entries;
    }

    public async Task<List<GalleryImage>> LoadAlbumAsync(string albumId, CancellationToken ct = default)
    {
        var metas = await GetImageMetasForAlbumAsync(albumId);
        var list = new List<GalleryImage>();
        foreach (var m in metas.OrderBy(x => x.sortOrder ?? 0).ThenBy(x => x.id))
        {
            var img = await LoadImageAsync(albumId, m.id, ct);
            if (img != null)
                list.Add(img);
        }
        return list;
    }

    public async Task<List<GalleryImage>> LoadAlbumThumbsAsync(string albumId, CancellationToken ct = default)
    {
        var metas = await GetImageMetasForAlbumAsync(albumId);
        var list = new List<GalleryImage>();
        foreach (var m in metas.OrderBy(x => x.sortOrder ?? 0).ThenBy(x => x.id))
        {
            if (!string.IsNullOrEmpty(m.deletedAt))
            {
                list.Add(new GalleryImage(
                    Id: m.id,
                    Name: m.name,
                    ContentType: m.contentType,
                    DataBase64: "",
                    Size: m.size,
                    Width: m.width,
                    Height: m.height,
                    Timestamp: DateTime.Parse(m.lastUpdated),
                    ModifiedAt: DateTime.Parse(m.lastUpdated),
                    DeletedAt: DateTime.Parse(m.deletedAt)));
                continue;
            }

            // Meta only — never decrypt full binary for the tile grid.
            list.Add(new GalleryImage(
                Id: m.id,
                Name: m.name,
                ContentType: m.contentType,
                DataBase64: "",
                Size: m.size,
                ThumbnailBase64: m.thumbnailBase64,
                Width: m.width,
                Height: m.height,
                Timestamp: DateTime.Parse(m.lastUpdated),
                ModifiedAt: DateTime.Parse(m.lastUpdated)));
        }
        return list;
    }

    private sealed class IdbiImageContentRecord
    {
        public object? content { get; set; }
        public string? format { get; set; }
    }

    public async Task<GalleryImage?> LoadImageAsync(string albumId, string imageId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var contentKey = ImageContentKey(ns, albumId, imageId);
        var metas = await GetImageMetasForAlbumAsync(albumId);
        var meta = metas.FirstOrDefault(m => string.Equals(m.id, imageId, StringComparison.OrdinalIgnoreCase));

        var rec = await _js.InvokeAsync<IdbiImageContentRecord?>("idbGetAlbumImageContent", contentKey);
        if (rec?.content is null)
        {
            if (meta != null && !string.IsNullOrEmpty(meta.deletedAt))
            {
                return new GalleryImage(
                    Id: imageId,
                    Name: meta.name,
                    ContentType: meta.contentType,
                    DataBase64: "",
                    Size: meta.size,
                    Width: meta.width,
                    Height: meta.height,
                    Timestamp: DateTime.Parse(meta.lastUpdated),
                    ModifiedAt: DateTime.Parse(meta.lastUpdated),
                    DeletedAt: DateTime.Parse(meta.deletedAt));
            }
            return null;
        }

        var keyB64 = await GetKeyAsync();
        var format = rec.format ?? "legacy";

        // bin-v1 and legacy both resolved in JS (avoids huge byte[] marshalling for bin path).
        if (string.Equals(format, "bin-v1", StringComparison.OrdinalIgnoreCase)
            || rec.content is not string)
        {
            var dataB64 = await _js.InvokeAsync<string?>("galleryLoadImageBase64FromIdb", contentKey, keyB64);
            if (string.IsNullOrEmpty(dataB64))
                return null;

            return new GalleryImage(
                Id: imageId,
                Name: meta?.name ?? imageId,
                ContentType: meta?.contentType ?? "image/jpeg",
                DataBase64: dataB64,
                Size: meta?.size ?? (long)(dataB64.Length * 0.75),
                ThumbnailBase64: meta?.thumbnailBase64,
                Width: meta?.width,
                Height: meta?.height,
                Timestamp: meta != null ? DateTime.Parse(meta.lastUpdated) : DateTime.UtcNow,
                ModifiedAt: meta != null ? DateTime.Parse(meta.lastUpdated) : DateTime.UtcNow);
        }

        // Legacy: encrypted JSON GalleryImage string stored in IDB
        var encrypted = rec.content as string;
        if (string.IsNullOrEmpty(encrypted))
            return null;

        var json = encrypted;
        if (!string.IsNullOrEmpty(keyB64))
            json = await _crypto.DecryptAsync(keyB64, encrypted);
        if (string.IsNullOrEmpty(json) || (json[0] != '{' && json[0] != '['))
            return null;

        var img = JsonSerializer.Deserialize<GalleryImage>(json);
        if (img == null) return null;
        if (meta != null)
        {
            img = img with
            {
                Name = meta.name,
                ThumbnailBase64 = img.ThumbnailBase64 ?? meta.thumbnailBase64,
                Width = img.Width ?? meta.width,
                Height = img.Height ?? meta.height
            };
        }
        return img;
    }

    public async Task<string?> CreateDisplayUrlAsync(string albumId, string imageId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var contentKey = ImageContentKey(ns, albumId, imageId);
        var meta = (await GetImageMetasForAlbumAsync(albumId))
            .FirstOrDefault(m => string.Equals(m.id, imageId, StringComparison.OrdinalIgnoreCase));
        var contentType = meta?.contentType ?? "image/jpeg";
        var keyB64 = await GetKeyAsync();
        try
        {
            return await _js.InvokeAsync<string?>("galleryCreateObjectUrlFromIdb", contentKey, keyB64, contentType);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmGallery] CreateDisplayUrl failed: {ex.Message}");
            return null;
        }
    }

    public async Task RevokeDisplayUrlAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { await _js.InvokeVoidAsync("galleryRevokeObjectUrl", url); }
        catch { /* ignore */ }
    }

    public async Task<long> SumStoredImageBytesAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        try
        {
            return await _js.InvokeAsync<long>("idbSumAlbumImageMetaSizes", ns);
        }
        catch
        {
            return 0;
        }
    }

    public async Task CreateAlbumAsync(string albumId, string title, CancellationToken ct = default)
    {
        var index = await LoadIndexAsync(ct);
        var sort = index.Count == 0 ? 0 : index.Max(a => a.SortOrder) + 1;
        await PersistAlbumMetaAsync(albumId, title, false, 0, sort, null, ct);
    }

    public async Task UpsertImageAsync(string albumId, GalleryImage image, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var album = await GetAlbumMetaAsync(albumId);
        if (album == null || !string.IsNullOrEmpty(album.deletedAt))
            throw new InvalidOperationException("Album not found: " + albumId);

        var contentKey = ImageContentKey(ns, albumId, image.Id);
        var keyB64 = await GetKeyAsync();

        if (image.DeletedAt.HasValue || string.IsNullOrEmpty(image.DataBase64))
        {
            // Tombstone / meta-only: drop binary payload
            try { await _js.InvokeVoidAsync("idbDeleteAlbumImageContent", contentKey); }
            catch { /* ok */ }
        }
        else
        {
            // Prefer raw-bytes path (sync apply still has base64).
            var raw = Convert.FromBase64String(image.DataBase64);
            await _js.InvokeVoidAsync("galleryStoreImageBinaryBytes", contentKey, keyB64, raw);
        }

        var now = DateTime.UtcNow.ToString("o");
        var fp = ImageFp(albumId, image);
        var existing = (await GetImageMetasForAlbumAsync(albumId))
            .FirstOrDefault(m => string.Equals(m.id, image.Id, StringComparison.OrdinalIgnoreCase));
        var sort = existing?.sortOrder ?? (await GetImageMetasForAlbumAsync(albumId)).Count;

        await _js.InvokeVoidAsync("idbPutAlbumImageMeta", new
        {
            key = ImageMetaKey(ns, albumId, image.Id),
            id = image.Id,
            albumId,
            @namespace = ns,
            name = image.Name,
            contentType = image.ContentType,
            size = image.Size > 0 ? image.Size : (string.IsNullOrEmpty(image.DataBase64) ? 0L : (long)(image.DataBase64.Length * 0.75)),
            width = image.Width,
            height = image.Height,
            lastUpdated = now,
            contentFingerprint = fp,
            deletedAt = image.DeletedAt?.ToString("o") ?? "",
            sortOrder = sort,
            thumbnailBase64 = image.ThumbnailBase64 ?? existing?.thumbnailBase64
        });

        await PersistAlbumMetaAsync(
            albumId,
            album.title,
            album.isPasswordProtected == true,
            album.protectionChangedTicks ?? 0,
            album.sortOrder ?? 0,
            null,
            ct);
    }

    private sealed class IngestUploadResult
    {
        public int width { get; set; }
        public int height { get; set; }
        public string? thumbnailBase64 { get; set; }
        public long size { get; set; }
        public string? contentSha256Base64 { get; set; }
    }

    public async Task<GalleryImage> UpsertImageFromRawBytesAsync(
        string albumId,
        string imageId,
        string name,
        string contentType,
        byte[] rawBytes,
        CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var album = await GetAlbumMetaAsync(albumId);
        if (album == null || !string.IsNullOrEmpty(album.deletedAt))
            throw new InvalidOperationException("Album not found: " + albumId);

        var contentKey = ImageContentKey(ns, albumId, imageId);
        var keyB64 = await GetKeyAsync();

        // One JS hop: thumb decode + AES-GCM + IDB put (no multi-MB base64 strings).
        var ingest = await _js.InvokeAsync<IngestUploadResult>(
            "galleryIngestUpload", contentKey, keyB64, rawBytes, contentType, 400);

        var size = ingest?.size > 0 ? ingest.size : rawBytes.LongLength;
        var fp = !string.IsNullOrEmpty(ingest?.contentSha256Base64)
            ? SyncFingerprint.ForAlbumImageHash(albumId, imageId, size, ingest!.contentSha256Base64!)
            : SyncFingerprint.ForAlbumImageRaw(albumId, imageId, size, rawBytes);

        var existing = (await GetImageMetasForAlbumAsync(albumId))
            .FirstOrDefault(m => string.Equals(m.id, imageId, StringComparison.OrdinalIgnoreCase));
        var sort = existing?.sortOrder ?? (await GetImageMetasForAlbumAsync(albumId)).Count;
        var now = DateTime.UtcNow;
        var nowIso = now.ToString("o");
        int? w = ingest is { width: > 0 } ? ingest.width : null;
        int? h = ingest is { height: > 0 } ? ingest.height : null;
        var thumb = ingest?.thumbnailBase64;

        await _js.InvokeVoidAsync("idbPutAlbumImageMeta", new
        {
            key = ImageMetaKey(ns, albumId, imageId),
            id = imageId,
            albumId,
            @namespace = ns,
            name,
            contentType,
            size,
            width = w,
            height = h,
            lastUpdated = nowIso,
            contentFingerprint = fp,
            deletedAt = "",
            sortOrder = sort,
            thumbnailBase64 = thumb
        });

        // Touch album index once (cheap meta write).
        await PersistAlbumMetaAsync(
            albumId,
            album.title,
            album.isPasswordProtected == true,
            album.protectionChangedTicks ?? 0,
            album.sortOrder ?? 0,
            null,
            ct);

        return new GalleryImage(
            Id: imageId,
            Name: name,
            ContentType: contentType,
            DataBase64: "",
            Size: size,
            ThumbnailBase64: thumb,
            Width: w,
            Height: h,
            Timestamp: now,
            ModifiedAt: now);
    }

    public async Task SoftDeleteImageAsync(string albumId, string imageId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await GetImageMetasForAlbumAsync(albumId);
        var meta = metas.FirstOrDefault(m => string.Equals(m.id, imageId, StringComparison.OrdinalIgnoreCase));
        if (meta == null)
            return;

        // Drop encrypted payload first.
        await _js.InvokeVoidAsync("idbDeleteAlbumImageContent", ImageContentKey(ns, albumId, imageId));

        var now = DateTime.UtcNow;
        var nowIso = now.ToString("o");
        await _js.InvokeVoidAsync("idbPutAlbumImageMeta", new
        {
            key = ImageMetaKey(ns, albumId, imageId),
            id = imageId,
            albumId,
            @namespace = ns,
            name = meta.name,
            contentType = meta.contentType,
            size = 0L,
            width = meta.width,
            height = meta.height,
            lastUpdated = nowIso,
            contentFingerprint = DeleteSyncPayload.AckValue(now.Ticks),
            deletedAt = nowIso,
            sortOrder = meta.sortOrder ?? 0,
            thumbnailBase64 = (string?)null
        });

        var album = await GetAlbumMetaAsync(albumId);
        if (album != null && string.IsNullOrEmpty(album.deletedAt))
        {
            await PersistAlbumMetaAsync(
                albumId,
                album.title,
                album.isPasswordProtected == true,
                album.protectionChangedTicks ?? 0,
                album.sortOrder ?? 0,
                null,
                ct);
        }
    }

    public async Task MoveImageAsync(string fromAlbumId, string toAlbumId, string imageId, CancellationToken ct = default)
    {
        var img = await LoadImageAsync(fromAlbumId, imageId, ct);
        if (img == null || img.DeletedAt.HasValue)
            return;
        var now = DateTime.UtcNow;
        var moved = img with { ModifiedAt = now, Timestamp = img.Timestamp ?? now };
        await UpsertImageAsync(toAlbumId, moved, ct);
        await SoftDeleteImageAsync(fromAlbumId, imageId, ct);
    }

    public async Task<DateTime> DeleteAlbumAsync(string albumId, CancellationToken ct = default)
    {
        var deletedAt = DateTime.UtcNow;
        var album = await GetAlbumMetaAsync(albumId);
        var title = album?.title ?? "(deleted)";
        var images = await GetImageMetasForAlbumAsync(albumId);
        var ns = GetPrefix();
        foreach (var m in images)
        {
            await _js.InvokeVoidAsync("idbDeleteAlbumImageContent", ImageContentKey(ns, albumId, m.id));
            await _js.InvokeVoidAsync("idbDeleteAlbumImageMeta", ImageMetaKey(ns, albumId, m.id));
        }
        // Best-effort purge of legacy whole-album content keys if any remain.
        try
        {
            await _js.InvokeVoidAsync("idbDeleteAlbumContent", AlbumMetaKey(ns, albumId));
        }
        catch { /* store may not exist */ }

        await PersistAlbumMetaAsync(
            albumId,
            title,
            album?.isPasswordProtected == true,
            album?.protectionChangedTicks ?? 0,
            album?.sortOrder ?? 0,
            deletedAt.ToString("o"),
            ct);
        return deletedAt;
    }

    public async Task<string?> GetMetaTitleAsync(string albumId, CancellationToken ct = default)
    {
        var m = await GetAlbumMetaAsync(albumId);
        return m?.title;
    }

    public async Task UpdateAlbumMetaAsync(string albumId, string title, CancellationToken ct = default)
    {
        var album = await GetAlbumMetaAsync(albumId);
        if (album == null || !string.IsNullOrEmpty(album.deletedAt))
            return;
        await PersistAlbumMetaAsync(
            albumId,
            title,
            album.isPasswordProtected == true,
            album.protectionChangedTicks ?? 0,
            album.sortOrder ?? 0,
            null,
            ct);
    }

    public async Task SetPasswordProtectedAsync(string albumId, bool isProtected, long? protectionChangedTicks = null, CancellationToken ct = default)
    {
        var album = await GetAlbumMetaAsync(albumId);
        if (album == null || !string.IsNullOrEmpty(album.deletedAt))
            return;
        var ticks = protectionChangedTicks is > 0 ? protectionChangedTicks.Value : DateTime.UtcNow.Ticks;
        await PersistAlbumMetaAsync(
            albumId,
            album.title,
            isProtected,
            ticks,
            album.sortOrder ?? 0,
            null,
            ct);
    }

    public async Task ReorderAlbumsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds.Count == 0) return;
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredAlbumMeta>>("idbGetAlbumMetasByNamespace", ns);
        var byId = metas.Where(m => string.IsNullOrEmpty(m.deletedAt))
            .ToDictionary(m => m.id, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (!byId.TryGetValue(orderedIds[i], out var m)) continue;
            await _js.InvokeVoidAsync("idbPutAlbumMeta", new
            {
                key = m.key,
                id = m.id,
                @namespace = m.@namespace,
                title = m.title,
                lastUpdated = m.lastUpdated,
                syncEnabled = m.syncEnabled,
                contentFingerprint = m.contentFingerprint,
                deletedAt = m.deletedAt ?? "",
                isPasswordProtected = m.isPasswordProtected == true,
                sortOrder = i,
                protectionChangedTicks = m.protectionChangedTicks ?? 0
            });
        }
    }

    public async Task<bool> ShouldAcceptIncomingAlbumMetaAsync(string albumId, long remoteLastUpdatedTicks, CancellationToken ct = default)
    {
        var meta = await GetAlbumMetaAsync(albumId);
        if (meta == null) return true;
        if (!string.IsNullOrEmpty(meta.deletedAt))
            return remoteLastUpdatedTicks > DateTime.Parse(meta.deletedAt).Ticks;
        return remoteLastUpdatedTicks >= DateTime.Parse(meta.lastUpdated).Ticks;
    }

    public async Task<bool> ShouldAcceptIncomingImageAsync(string albumId, GalleryImage image, CancellationToken ct = default)
    {
        // Fast path: meta only — never decrypt multi-MB just to reject stale/equal.
        var metas = await GetImageMetasForAlbumAsync(albumId);
        var meta = metas.FirstOrDefault(m => string.Equals(m.id, image.Id, StringComparison.OrdinalIgnoreCase));
        if (meta == null || !string.IsNullOrEmpty(meta.deletedAt))
            return true;

        var remoteFp = ImageFp(albumId, image);
        if (!string.IsNullOrEmpty(meta.contentFingerprint)
            && string.Equals(meta.contentFingerprint, remoteFp, StringComparison.Ordinal))
            return false; // identical content already stored

        var localTicks = DateTime.Parse(meta.lastUpdated).Ticks;
        var remoteTicks = ImageTicks(image);
        // Strict greater — equal means already applied (avoids re-encrypt + UI thrash).
        return remoteTicks > localTicks;
    }

    public async Task<bool> TryApplyRemoteAlbumDeleteAsync(string albumId, long deletedAtTicks, CancellationToken ct = default)
    {
        var meta = await GetAlbumMetaAsync(albumId);
        if (meta == null) return false;
        if (!string.IsNullOrEmpty(meta.deletedAt) && DateTime.Parse(meta.deletedAt).Ticks >= deletedAtTicks)
            return false;
        if (string.IsNullOrEmpty(meta.deletedAt) && DateTime.Parse(meta.lastUpdated).Ticks > deletedAtTicks)
            return false;
        await DeleteAlbumAsync(albumId, ct);
        return true;
    }

    public async Task<bool> TryApplyRemoteImageDeleteAsync(string albumId, string imageId, long deletedAtTicks, CancellationToken ct = default)
    {
        var local = await LoadImageAsync(albumId, imageId, ct);
        if (local == null)
        {
            // Create tombstone so we don't re-request forever
            var tomb = new GalleryImage(
                Id: imageId,
                Name: "(deleted)",
                ContentType: "application/octet-stream",
                DataBase64: "",
                Size: 0,
                Timestamp: new DateTime(deletedAtTicks, DateTimeKind.Utc),
                ModifiedAt: new DateTime(deletedAtTicks, DateTimeKind.Utc),
                DeletedAt: new DateTime(deletedAtTicks, DateTimeKind.Utc));
            try { await UpsertImageAsync(albumId, tomb, ct); } catch { /* album may not exist */ }
            return true;
        }
        if (ImageTicks(local) > deletedAtTicks)
            return false;
        var now = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        await UpsertImageAsync(albumId, local with { DeletedAt = now, ModifiedAt = now, DataBase64 = "", ThumbnailBase64 = null }, ct);
        var ns = GetPrefix();
        await _js.InvokeVoidAsync("idbDeleteAlbumImageContent", ImageContentKey(ns, albumId, imageId));
        return true;
    }

    public async Task ApplyRemoteAlbumMetaAsync(
        string albumId,
        string title,
        bool? isPasswordProtected,
        long? protectionChangedTicks,
        CancellationToken ct = default)
    {
        var existing = await GetAlbumMetaAsync(albumId);
        if (existing == null)
        {
            await CreateAlbumAsync(albumId, title, ct);
            existing = await GetAlbumMetaAsync(albumId);
        }
        if (existing == null || !string.IsNullOrEmpty(existing.deletedAt))
            return;

        var localProtected = existing.isPasswordProtected == true;
        var localTicks = existing.protectionChangedTicks ?? 0;
        var applyProtected = localProtected;
        var applyTicks = localTicks;
        if (PasswordProtectionSync.TryResolve(
                isPasswordProtected,
                protectionChangedTicks,
                localProtected,
                localTicks,
                out var p,
                out var t))
        {
            applyProtected = p;
            applyTicks = t;
        }

        await PersistAlbumMetaAsync(
            albumId,
            ChatMessageHelper.ResolveIncomingNoteTitle(title, existing.title),
            applyProtected,
            applyTicks,
            existing.sortOrder ?? 0,
            null,
            ct);
    }
}
