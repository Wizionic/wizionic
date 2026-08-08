using App.Core.Storage;

namespace App.Shared.Services.Tools;

/// <summary>
/// Shared album resolve + image persist for the save_to_gallery tool and auto-save paths
/// (Omni / direct image models cannot call client tools).
/// </summary>
public static class GallerySaveHelper
{
    /// <summary>
    /// Fuzzy match: exact title (ignore case), else best contains match, else create.
    /// </summary>
    public static (string AlbumId, string Title, bool Created) ResolveOrCreateAlbum(
        IReadOnlyList<LocalAlbum> albums,
        string query)
    {
        var live = albums.Where(a => !string.IsNullOrWhiteSpace(a.Title)).ToList();

        var exact = live.FirstOrDefault(a =>
            string.Equals(a.Title.Trim(), query, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return (exact.Id, exact.Title, false);

        var contains = live
            .Where(a => a.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || query.Contains(a.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Title.Length)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (contains != null)
            return (contains.Id, contains.Title, false);

        var title = NormalizeNewAlbumTitle(query);
        return (Guid.NewGuid().ToString("N"), title, true);
    }

    public static string NormalizeNewAlbumTitle(string query)
    {
        var t = query.Trim();
        if (t.StartsWith("my ", StringComparison.OrdinalIgnoreCase) && t.Length > 3)
            t = t[3..].Trim();
        if (t.EndsWith(" gallery", StringComparison.OrdinalIgnoreCase) && t.Length > 8)
            t = t[..^8].Trim();
        else if (t.EndsWith(" album", StringComparison.OrdinalIgnoreCase) && t.Length > 6)
            t = t[..^6].Trim();
        if (string.IsNullOrWhiteSpace(t))
            t = query.Trim();
        if (t.Length > 0)
            t = char.ToUpperInvariant(t[0]) + (t.Length > 1 ? t[1..] : "");
        return t;
    }

    public static async Task<(bool Ok, string Message)> SaveImageAsync(
        IGalleryStore gallery,
        IGallerySyncBridge? syncBridge,
        IStorageQuotaService? quota,
        string albumQuery,
        byte[] raw,
        string contentType,
        string imageName,
        CancellationToken ct = default)
    {
        if (raw.Length == 0)
            return (false, "Empty image.");

        if (quota != null && !await quota.CanAcceptBytesAsync(raw.LongLength))
            return (false, "Not enough storage under the device limit. Raise the limit on the Sync page.");

        var albums = await gallery.LoadIndexAsync();
        var (albumId, albumTitle, created) = ResolveOrCreateAlbum(albums, albumQuery.Trim());

        if (created)
        {
            await gallery.CreateAlbumAsync(albumId, albumTitle);
            syncBridge?.ScheduleAutoSyncAlbumMetaAfterLocalSave(albumId, albumTitle);
        }

        var imageId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            contentType = "image/png";
        if (string.IsNullOrWhiteSpace(imageName))
            imageName = "saved-image.png";

        await gallery.UpsertImageFromRawBytesAsync(albumId, imageId, imageName, contentType, raw);
        syncBridge?.ScheduleAutoSyncAlbumImageAfterLocalSave(albumId, imageId);
        syncBridge?.ScheduleAutoSyncAlbumMetaAfterLocalSave(albumId, albumTitle);

        var size = raw.LongLength < 1024 ? $"{raw.LongLength} B"
            : raw.LongLength < 1024 * 1024 ? $"{raw.LongLength / 1024.0:0.#} KB"
            : $"{raw.LongLength / (1024.0 * 1024.0):0.#} MB";

        return (true,
            $"Saved image \"{imageName}\" ({size}) to album \"{albumTitle}\"" +
            (created ? " (created)." : "."));
    }
}
