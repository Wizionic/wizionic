using App.Core.Auth;
using App.Core.Storage;
using Microsoft.JSInterop;
using System.Text.Json;

namespace App.Maui.Services;

public class SqliteGalleryStore : IGalleryStore
{
    private const string AlbumPrefix = "a-wasmchat-album-";
    private const string ImagePrefix = "a-wasmchat-img-";
    /// <summary>Content prefix: encrypted raw image bytes as base64 (not JSON).</summary>
    private const string BinV1Prefix = "BIN1:";

    private readonly IAuthService _auth;
    private readonly ICryptoService _crypto;
    private readonly MauiCryptoService _mauiCrypto;
    private readonly SqliteHistoryDatabase _db;
    private readonly IJSRuntime _js;

    public SqliteGalleryStore(
        IAuthService auth,
        ICryptoService crypto,
        MauiCryptoService mauiCrypto,
        SqliteHistoryDatabase db,
        IJSRuntime js)
    {
        _auth = auth;
        _crypto = crypto;
        _mauiCrypto = mauiCrypto;
        _db = db;
        _js = js;
    }

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private async Task<string> GetKeyAsync() =>
        await _auth.GetOrCreateHistoryEncryptionKeyAsync();

    private string AlbumMetaKey(string ns, string albumId) => ns + AlbumPrefix + albumId;
    private string ImageContentKey(string ns, string albumId, string imageId) =>
        ns + ImagePrefix + "c-" + albumId + "-" + imageId;

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
        var ns = GetPrefix();
        var metas = await _db.GetAlbumImageMetasByAlbumAsync(ns, albumId, ct);
        var refs = new List<AlbumImageRef>();
        var order = 0;
        foreach (var m in metas.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
        {
            long? delTicks = null;
            if (!string.IsNullOrEmpty(m.DeletedAt))
                delTicks = DateTime.Parse(m.DeletedAt).Ticks;
            var fp = m.ContentFingerprint ?? "";
            if (string.IsNullOrEmpty(fp) && string.IsNullOrEmpty(m.DeletedAt))
            {
                var img = await LoadImageAsync(albumId, m.Id, ct);
                if (img != null) fp = ImageFp(albumId, img);
            }
            refs.Add(new AlbumImageRef(m.Id, fp, order++, delTicks));
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

        await _db.UpsertAlbumMetaAsync(new SqliteHistoryDatabase.AlbumMetaRow(
            AlbumMetaKey(ns, albumId),
            albumId,
            ns,
            normalizedTitle,
            DateTime.UtcNow.ToString("o"),
            syncEnabled,
            fp,
            deletedAt,
            isProtected,
            sortOrder,
            proticks), ct);
    }

    public async Task<List<LocalAlbum>> LoadIndexAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetAlbumMetasByNamespaceAsync(ns, ct);
        return metas
            .Where(m => string.IsNullOrEmpty(m.DeletedAt))
            .OrderBy(m => m.SortOrder)
            .ThenByDescending(m => m.LastUpdated)
            .Select(m => new LocalAlbum(
                m.Id,
                string.IsNullOrWhiteSpace(m.Title) ? "(empty)" : m.Title,
                DateTime.Parse(m.LastUpdated),
                m.IsPasswordProtected,
                m.SortOrder,
                m.ProtectionChangedTicks))
            .ToList();
    }

    public async Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetAlbumMetasByNamespaceAsync(ns, ct);
        var entries = new List<SyncManifestEntry>();
        foreach (var m in metas)
        {
            var title = string.IsNullOrWhiteSpace(m.Title) ? "(empty)" : m.Title;
            long? deletedAtTicks = null;
            if (!string.IsNullOrEmpty(m.DeletedAt))
                deletedAtTicks = DateTime.Parse(m.DeletedAt).Ticks;

            var fingerprint = deletedAtTicks.HasValue
                ? DeleteSyncPayload.AckValue(deletedAtTicks.Value)
                : m.ContentFingerprint ?? "";

            if (!deletedAtTicks.HasValue && (backfillMissingFingerprints || string.IsNullOrEmpty(fingerprint)))
            {
                var refs = await BuildImageRefsAsync(m.Id, ct);
                fingerprint = SyncFingerprint.ForAlbumMeta(
                    m.Id, title, refs, m.IsPasswordProtected, m.ProtectionChangedTicks);
                if (backfillMissingFingerprints)
                    await _db.UpsertAlbumMetaAsync(m with { ContentFingerprint = fingerprint }, ct);
            }

            entries.Add(new SyncManifestEntry(
                m.Id, title, DateTime.Parse(m.LastUpdated).Ticks, fingerprint, deletedAtTicks));
        }
        return entries;
    }

    public async Task<List<SyncManifestEntry>> LoadImageManifestEntriesAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetAlbumImageMetasByNamespaceAsync(ns, ct);
        var entries = new List<SyncManifestEntry>();
        foreach (var m in metas)
        {
            long? deletedAtTicks = null;
            if (!string.IsNullOrEmpty(m.DeletedAt))
                deletedAtTicks = DateTime.Parse(m.DeletedAt).Ticks;

            var composite = GalleryImageSyncPayload.CompositeId(m.AlbumId, m.Id);
            var fingerprint = deletedAtTicks.HasValue
                ? DeleteSyncPayload.AckValue(deletedAtTicks.Value)
                : m.ContentFingerprint ?? "";

            if (!deletedAtTicks.HasValue && string.IsNullOrEmpty(fingerprint))
            {
                var img = await LoadImageAsync(m.AlbumId, m.Id, ct);
                if (img != null) fingerprint = ImageFp(m.AlbumId, img);
            }

            entries.Add(new SyncManifestEntry(
                composite, m.Name, DateTime.Parse(m.LastUpdated).Ticks, fingerprint, deletedAtTicks));
        }
        return entries;
    }

    public async Task<List<GalleryImage>> LoadAlbumAsync(string albumId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetAlbumImageMetasByAlbumAsync(ns, albumId, ct);
        var list = new List<GalleryImage>();
        foreach (var m in metas.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
        {
            var img = await LoadImageAsync(albumId, m.Id, ct);
            if (img != null) list.Add(img);
        }
        return list;
    }

    public async Task<List<GalleryImage>> LoadAlbumThumbsAsync(string albumId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetAlbumImageMetasByAlbumAsync(ns, albumId, ct);
        var list = new List<GalleryImage>();
        foreach (var m in metas.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
        {
            if (!string.IsNullOrEmpty(m.DeletedAt))
            {
                list.Add(new GalleryImage(
                    Id: m.Id,
                    Name: m.Name,
                    ContentType: m.ContentType,
                    DataBase64: "",
                    Size: m.Size,
                    Width: m.Width,
                    Height: m.Height,
                    Timestamp: DateTime.Parse(m.LastUpdated),
                    ModifiedAt: DateTime.Parse(m.LastUpdated),
                    DeletedAt: DateTime.Parse(m.DeletedAt)));
                continue;
            }

            // Meta only — never decrypt full binary for the tile grid.
            list.Add(new GalleryImage(
                Id: m.Id,
                Name: m.Name,
                ContentType: m.ContentType,
                DataBase64: "",
                Size: m.Size,
                ThumbnailBase64: m.ThumbnailBase64,
                Width: m.Width,
                Height: m.Height,
                Timestamp: DateTime.Parse(m.LastUpdated),
                ModifiedAt: DateTime.Parse(m.LastUpdated)));
        }
        return list;
    }

    public async Task<GalleryImage?> LoadImageAsync(string albumId, string imageId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var meta = await _db.GetAlbumImageMetaAsync(ns, albumId, imageId, ct);
        var encrypted = await _db.GetAlbumImageContentAsync(ImageContentKey(ns, albumId, imageId), ct);
        if (string.IsNullOrEmpty(encrypted))
        {
            if (meta != null && !string.IsNullOrEmpty(meta.DeletedAt))
            {
                return new GalleryImage(
                    Id: imageId,
                    Name: meta.Name,
                    ContentType: meta.ContentType,
                    DataBase64: "",
                    Size: meta.Size,
                    Width: meta.Width,
                    Height: meta.Height,
                    Timestamp: DateTime.Parse(meta.LastUpdated),
                    ModifiedAt: DateTime.Parse(meta.LastUpdated),
                    DeletedAt: DateTime.Parse(meta.DeletedAt));
            }
            return null;
        }

        var keyB64 = await GetKeyAsync();

        // BIN1: encrypted raw image bytes (base64 of AES-GCM package)
        if (encrypted.StartsWith(BinV1Prefix, StringComparison.Ordinal))
        {
            var cipherB64 = encrypted[BinV1Prefix.Length..];
            var cipher = Convert.FromBase64String(cipherB64);
            byte[] plain;
            if (!string.IsNullOrEmpty(keyB64))
                plain = _mauiCrypto.DecryptBytes(keyB64, cipher);
            else
                plain = cipher;
            var dataB64 = Convert.ToBase64String(plain);
            return new GalleryImage(
                Id: imageId,
                Name: meta?.Name ?? imageId,
                ContentType: meta?.ContentType ?? "image/jpeg",
                DataBase64: dataB64,
                Size: meta?.Size ?? plain.Length,
                ThumbnailBase64: meta?.ThumbnailBase64,
                Width: meta?.Width,
                Height: meta?.Height,
                Timestamp: meta != null ? DateTime.Parse(meta.LastUpdated) : DateTime.UtcNow,
                ModifiedAt: meta != null ? DateTime.Parse(meta.LastUpdated) : DateTime.UtcNow);
        }

        // Legacy JSON GalleryImage
        var json = encrypted;
        if (!string.IsNullOrEmpty(keyB64))
            json = await _crypto.DecryptAsync(keyB64, encrypted, ct);
        if (string.IsNullOrEmpty(json)) return null;
        var img = JsonSerializer.Deserialize<GalleryImage>(json);
        if (img == null) return null;
        if (meta != null)
        {
            img = img with
            {
                Name = meta.Name,
                ThumbnailBase64 = img.ThumbnailBase64 ?? meta.ThumbnailBase64,
                Width = img.Width ?? meta.Width,
                Height = img.Height ?? meta.Height
            };
        }
        return img;
    }

    public async Task<string?> CreateDisplayUrlAsync(string albumId, string imageId, CancellationToken ct = default)
    {
        try
        {
            var img = await LoadImageAsync(albumId, imageId, ct);
            if (img == null || string.IsNullOrEmpty(img.DataBase64))
                return null;
            return await _js.InvokeAsync<string?>("galleryObjectUrlFromBase64", img.DataBase64, img.ContentType);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SqliteGallery] CreateDisplayUrl failed: {ex.Message}");
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
        var metas = await _db.GetAlbumImageMetasByNamespaceAsync(ns, ct);
        long sum = 0;
        foreach (var m in metas)
        {
            if (!string.IsNullOrEmpty(m.DeletedAt)) continue;
            if (m.Size > 0) sum += m.Size;
        }
        return sum;
    }

    public async Task CreateAlbumAsync(string albumId, string title, CancellationToken ct = default)
    {
        var index = await LoadIndexAsync(ct);
        // My Media is virtual — pin first; only meta (protection) is stored, never images.
        var sort = GalleryConstants.IsMyMediaAlbum(albumId)
            ? -1
            : (index.Count == 0 ? 0 : index.Max(a => a.SortOrder) + 1);
        var albumTitle = GalleryConstants.IsMyMediaAlbum(albumId)
            ? GalleryConstants.MyMediaAlbumTitle
            : title;
        await PersistAlbumMetaAsync(albumId, albumTitle, false, 0, sort, null, ct);
    }

    public async Task UpsertImageAsync(string albumId, GalleryImage image, CancellationToken ct = default)
    {
        if (GalleryConstants.IsMyMediaAlbum(albumId))
            throw new InvalidOperationException("My Media is a virtual album; images cannot be added directly.");

        var ns = GetPrefix();
        var album = await _db.GetAlbumMetaByIdAsync(ns, albumId, ct);
        if (album == null || !string.IsNullOrEmpty(album.DeletedAt))
            throw new InvalidOperationException("Album not found: " + albumId);

        var contentKey = ImageContentKey(ns, albumId, image.Id);
        var keyB64 = await GetKeyAsync();

        if (image.DeletedAt.HasValue || string.IsNullOrEmpty(image.DataBase64))
        {
            await _db.DeleteAlbumImageContentAsync(contentKey, ct);
        }
        else
        {
            // BIN1: encrypt raw image bytes (not nested JSON/base64).
            var plain = Convert.FromBase64String(image.DataBase64);
            byte[] cipher;
            if (!string.IsNullOrEmpty(keyB64))
                cipher = _mauiCrypto.EncryptBytes(keyB64, plain);
            else
                cipher = plain;
            var toStore = BinV1Prefix + Convert.ToBase64String(cipher);
            await _db.SetAlbumImageContentAsync(contentKey, toStore, ct);
        }

        var existing = await _db.GetAlbumImageMetaAsync(ns, albumId, image.Id, ct);
        var sort = existing?.SortOrder ?? (await _db.GetAlbumImageMetasByAlbumAsync(ns, albumId, ct)).Count;
        var now = DateTime.UtcNow.ToString("o");
        var fp = ImageFp(albumId, image);
        var thumb = image.ThumbnailBase64 ?? existing?.ThumbnailBase64;
        var size = image.Size > 0 ? image.Size : (long)(image.DataBase64?.Length * 0.75 ?? 0);

        await _db.UpsertAlbumImageMetaAsync(new SqliteHistoryDatabase.AlbumImageMetaRow(
            contentKey + "-meta",
            image.Id,
            albumId,
            ns,
            image.Name,
            image.ContentType,
            size,
            image.Width,
            image.Height,
            now,
            fp,
            image.DeletedAt?.ToString("o"),
            sort,
            thumb), ct);

        await PersistAlbumMetaAsync(
            albumId, album.Title, album.IsPasswordProtected, album.ProtectionChangedTicks, album.SortOrder, null, ct);
    }

    public async Task<GalleryImage> UpsertImageFromRawBytesAsync(
        string albumId,
        string imageId,
        string name,
        string contentType,
        byte[] rawBytes,
        CancellationToken ct = default)
    {
        if (GalleryConstants.IsMyMediaAlbum(albumId))
            throw new InvalidOperationException("My Media is a virtual album; images cannot be added directly.");

        // MAUI: thumb via JS, encrypt native — still much faster than WASM base64 path.
        // Tool saves often run outside the Blazor circuit scope (IServiceScopeFactory), so
        // IJSRuntime may fail; always fall back so the thumbs-only grid can still render.
        int? w = null, h = null;
        string? thumb = null;
        var b64 = Convert.ToBase64String(rawBytes);
        try
        {
            var prep = await _js.InvokeAsync<PrepResult?>("galleryPrepareImage", b64, contentType, 400);
            if (prep != null)
            {
                if (prep.width > 0) w = prep.width;
                if (prep.height > 0) h = prep.height;
                thumb = prep.thumbnailBase64;
            }
        }
        catch { /* optional — common for tool-path scopes without circuit JS */ }

        if (string.IsNullOrEmpty(thumb) && rawBytes.Length > 0)
            thumb = b64; // full image as tile source (ThumbnailUrl sniffs PNG/JPEG)

        var now = DateTime.UtcNow;
        var gi = new GalleryImage(
            Id: imageId,
            Name: name,
            ContentType: contentType,
            DataBase64: b64,
            Size: rawBytes.LongLength,
            ThumbnailBase64: thumb,
            Width: w,
            Height: h,
            Timestamp: now,
            ModifiedAt: now);
        await UpsertImageAsync(albumId, gi, ct);
        return gi with { DataBase64 = "" };
    }

    private sealed class PrepResult
    {
        public int width { get; set; }
        public int height { get; set; }
        public string? thumbnailBase64 { get; set; }
    }

    public async Task SoftDeleteImageAsync(string albumId, string imageId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var meta = await _db.GetAlbumImageMetaAsync(ns, albumId, imageId, ct);
        if (meta == null) return;

        // Drop encrypted payload first so disk usage falls immediately.
        await _db.DeleteAlbumImageContentAsync(ImageContentKey(ns, albumId, imageId), ct);

        var now = DateTime.UtcNow;
        var fp = DeleteSyncPayload.AckValue(now.Ticks);
        await _db.UpsertAlbumImageMetaAsync(meta with
        {
            DeletedAt = now.ToString("o"),
            LastUpdated = now.ToString("o"),
            ContentFingerprint = fp,
            Size = 0,
            ThumbnailBase64 = null
        }, ct);

        var album = await _db.GetAlbumMetaByIdAsync(ns, albumId, ct);
        if (album != null && string.IsNullOrEmpty(album.DeletedAt))
        {
            await PersistAlbumMetaAsync(
                albumId, album.Title, album.IsPasswordProtected, album.ProtectionChangedTicks, album.SortOrder, null, ct);
        }

        // Reclaim freelist pages when large blobs were removed.
        try { await _db.VacuumAsync(ct); } catch { /* non-fatal */ }
    }

    public async Task MoveImageAsync(string fromAlbumId, string toAlbumId, string imageId, CancellationToken ct = default)
    {
        if (GalleryConstants.IsMyMediaAlbum(fromAlbumId) || GalleryConstants.IsMyMediaAlbum(toAlbumId))
            throw new InvalidOperationException("My Media is virtual; use copy-to-album from the gallery UI.");

        var img = await LoadImageAsync(fromAlbumId, imageId, ct);
        if (img == null || img.DeletedAt.HasValue) return;
        var now = DateTime.UtcNow;
        await UpsertImageAsync(toAlbumId, img with { ModifiedAt = now, Timestamp = img.Timestamp ?? now }, ct);
        await SoftDeleteImageAsync(fromAlbumId, imageId, ct);
    }

    public async Task<DateTime> DeleteAlbumAsync(string albumId, CancellationToken ct = default)
    {
        if (GalleryConstants.IsMyMediaAlbum(albumId))
            throw new InvalidOperationException("My Media cannot be deleted.");

        var deletedAt = DateTime.UtcNow;
        var ns = GetPrefix();
        var album = await _db.GetAlbumMetaByIdAsync(ns, albumId, ct);
        var title = album?.Title ?? "(deleted)";
        var images = await _db.GetAlbumImageMetasByAlbumAsync(ns, albumId, ct);
        foreach (var m in images)
        {
            await _db.DeleteAlbumImageContentAsync(ImageContentKey(ns, albumId, m.Id), ct);
            await _db.DeleteAlbumImageMetaAsync(m.StorageKey, ct);
        }
        // Clear any leftover whole-album blobs from the first gallery implementation.
        try { await _db.PurgeLegacyAlbumContentAsync(ct); } catch { /* ignore */ }

        await PersistAlbumMetaAsync(
            albumId, title, album?.IsPasswordProtected ?? false, album?.ProtectionChangedTicks ?? 0,
            album?.SortOrder ?? 0, deletedAt.ToString("o"), ct);

        try { await _db.VacuumAsync(ct); } catch { /* non-fatal if another connection holds the DB */ }
        return deletedAt;
    }

    public async Task<string?> GetMetaTitleAsync(string albumId, CancellationToken ct = default) =>
        (await _db.GetAlbumMetaByIdAsync(GetPrefix(), albumId, ct))?.Title;

    public async Task UpdateAlbumMetaAsync(string albumId, string title, CancellationToken ct = default)
    {
        var album = await _db.GetAlbumMetaByIdAsync(GetPrefix(), albumId, ct);
        if (album == null || !string.IsNullOrEmpty(album.DeletedAt)) return;
        // My Media title is fixed (virtual album).
        var nextTitle = GalleryConstants.IsMyMediaAlbum(albumId)
            ? GalleryConstants.MyMediaAlbumTitle
            : title;
        await PersistAlbumMetaAsync(albumId, nextTitle, album.IsPasswordProtected, album.ProtectionChangedTicks, album.SortOrder, null, ct);
    }

    public async Task SetPasswordProtectedAsync(string albumId, bool isProtected, long? protectionChangedTicks = null, CancellationToken ct = default)
    {
        var album = await _db.GetAlbumMetaByIdAsync(GetPrefix(), albumId, ct);
        if (album == null || !string.IsNullOrEmpty(album.DeletedAt)) return;
        var ticks = protectionChangedTicks is > 0 ? protectionChangedTicks.Value : DateTime.UtcNow.Ticks;
        await PersistAlbumMetaAsync(albumId, album.Title, isProtected, ticks, album.SortOrder, null, ct);
    }

    public async Task ReorderAlbumsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds.Count == 0) return;
        var ns = GetPrefix();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var existing = await _db.GetAlbumMetaByIdAsync(ns, orderedIds[i], ct);
            if (existing == null) continue;
            await _db.UpsertAlbumMetaAsync(existing with { SortOrder = i }, ct);
        }
    }

    public async Task<bool> ShouldAcceptIncomingAlbumMetaAsync(string albumId, long remoteLastUpdatedTicks, CancellationToken ct = default)
    {
        var meta = await _db.GetAlbumMetaByIdAsync(GetPrefix(), albumId, ct);
        if (meta == null) return true;
        if (!string.IsNullOrEmpty(meta.DeletedAt))
            return remoteLastUpdatedTicks > DateTime.Parse(meta.DeletedAt).Ticks;
        return remoteLastUpdatedTicks >= DateTime.Parse(meta.LastUpdated).Ticks;
    }

    public async Task<bool> ShouldAcceptIncomingImageAsync(string albumId, GalleryImage image, CancellationToken ct = default)
    {
        var meta = await _db.GetAlbumImageMetaAsync(GetPrefix(), albumId, image.Id, ct);
        if (meta == null || !string.IsNullOrEmpty(meta.DeletedAt))
            return true;

        var remoteFp = ImageFp(albumId, image);
        if (!string.IsNullOrEmpty(meta.ContentFingerprint)
            && string.Equals(meta.ContentFingerprint, remoteFp, StringComparison.Ordinal))
            return false;

        var localTicks = DateTime.Parse(meta.LastUpdated).Ticks;
        return ImageTicks(image) > localTicks;
    }

    public async Task<bool> TryApplyRemoteAlbumDeleteAsync(string albumId, long deletedAtTicks, CancellationToken ct = default)
    {
        var meta = await _db.GetAlbumMetaByIdAsync(GetPrefix(), albumId, ct);
        if (meta == null) return false;
        if (!string.IsNullOrEmpty(meta.DeletedAt) && DateTime.Parse(meta.DeletedAt).Ticks >= deletedAtTicks)
            return false;
        if (string.IsNullOrEmpty(meta.DeletedAt) && DateTime.Parse(meta.LastUpdated).Ticks > deletedAtTicks)
            return false;
        await DeleteAlbumAsync(albumId, ct);
        return true;
    }

    public async Task<bool> TryApplyRemoteImageDeleteAsync(string albumId, string imageId, long deletedAtTicks, CancellationToken ct = default)
    {
        var local = await LoadImageAsync(albumId, imageId, ct);
        if (local == null)
        {
            var tomb = new GalleryImage(
                Id: imageId, Name: "(deleted)", ContentType: "application/octet-stream", DataBase64: "",
                Size: 0,
                Timestamp: new DateTime(deletedAtTicks, DateTimeKind.Utc),
                ModifiedAt: new DateTime(deletedAtTicks, DateTimeKind.Utc),
                DeletedAt: new DateTime(deletedAtTicks, DateTimeKind.Utc));
            try { await UpsertImageAsync(albumId, tomb, ct); } catch { }
            return true;
        }
        if (ImageTicks(local) > deletedAtTicks) return false;
        var now = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        await UpsertImageAsync(albumId, local with { DeletedAt = now, ModifiedAt = now, DataBase64 = "", ThumbnailBase64 = null }, ct);
        await _db.DeleteAlbumImageContentAsync(ImageContentKey(GetPrefix(), albumId, imageId), ct);
        return true;
    }

    public async Task ApplyRemoteAlbumMetaAsync(
        string albumId,
        string title,
        bool? isPasswordProtected,
        long? protectionChangedTicks,
        CancellationToken ct = default)
    {
        var existing = await _db.GetAlbumMetaByIdAsync(GetPrefix(), albumId, ct);
        if (existing == null)
        {
            await CreateAlbumAsync(albumId, title, ct);
            existing = await _db.GetAlbumMetaByIdAsync(GetPrefix(), albumId, ct);
        }
        if (existing == null || !string.IsNullOrEmpty(existing.DeletedAt)) return;

        var localProtected = existing.IsPasswordProtected;
        var localTicks = existing.ProtectionChangedTicks;
        var applyProtected = localProtected;
        var applyTicks = localTicks;
        if (PasswordProtectionSync.TryResolve(
                isPasswordProtected, protectionChangedTicks, localProtected, localTicks, out var p, out var t))
        {
            applyProtected = p;
            applyTicks = t;
        }

        await PersistAlbumMetaAsync(
            albumId,
            ChatMessageHelper.ResolveIncomingNoteTitle(title, existing.Title),
            applyProtected,
            applyTicks,
            existing.SortOrder,
            null,
            ct);
    }
}
