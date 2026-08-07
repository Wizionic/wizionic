namespace App.Core.Storage;

public interface IGalleryStore
{
    Task<List<LocalAlbum>> LoadIndexAsync(CancellationToken ct = default);
    /// <summary>Album meta manifest (title/protection/image-id set — no image bytes).</summary>
    Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default);
    /// <summary>Per-image manifest entries. Id format: <c>albumId/imageId</c>.</summary>
    Task<List<SyncManifestEntry>> LoadImageManifestEntriesAsync(CancellationToken ct = default);

    /// <summary>Full images including DataBase64 (use sparingly on WASM).</summary>
    Task<List<GalleryImage>> LoadAlbumAsync(string albumId, CancellationToken ct = default);
    /// <summary>
    /// Album listing without full-resolution payloads — thumbs + metadata only.
    /// Prefer this for grid UI so Blazor/WASM does not hold multi-MB data URLs for every tile.
    /// </summary>
    Task<List<GalleryImage>> LoadAlbumThumbsAsync(string albumId, CancellationToken ct = default);
    Task<GalleryImage?> LoadImageAsync(string albumId, string imageId, CancellationToken ct = default);

    /// <summary>
    /// Short-lived blob/object URL for lightbox display — avoids multi-MB data: URLs in Blazor markup.
    /// Caller must <see cref="RevokeDisplayUrlAsync"/> when done.
    /// </summary>
    Task<string?> CreateDisplayUrlAsync(string albumId, string imageId, CancellationToken ct = default);

    /// <summary>Revoke a URL from <see cref="CreateDisplayUrlAsync"/>.</summary>
    Task RevokeDisplayUrlAsync(string url, CancellationToken ct = default);

    /// <summary>Sum of stored image sizes (meta only — never decrypts payloads). Used for quota.</summary>
    Task<long> SumStoredImageBytesAsync(CancellationToken ct = default);

    /// <summary>Create empty album meta (no images).</summary>
    Task CreateAlbumAsync(string albumId, string title, CancellationToken ct = default);

    Task UpsertImageAsync(string albumId, GalleryImage image, CancellationToken ct = default);

    /// <summary>
    /// Fast local upload path: encrypt + store from raw bytes (WASM does thumb/encrypt in JS).
    /// Returns meta-only GalleryImage (empty DataBase64) for UI binding.
    /// </summary>
    Task<GalleryImage> UpsertImageFromRawBytesAsync(
        string albumId,
        string imageId,
        string name,
        string contentType,
        byte[] rawBytes,
        CancellationToken ct = default);

    Task SoftDeleteImageAsync(string albumId, string imageId, CancellationToken ct = default);
    Task MoveImageAsync(string fromAlbumId, string toAlbumId, string imageId, CancellationToken ct = default);

    Task<DateTime> DeleteAlbumAsync(string albumId, CancellationToken ct = default);

    Task<string?> GetMetaTitleAsync(string albumId, CancellationToken ct = default);
    Task UpdateAlbumMetaAsync(string albumId, string title, CancellationToken ct = default);

    Task SetPasswordProtectedAsync(string albumId, bool isProtected, long? protectionChangedTicks = null, CancellationToken ct = default);
    Task ReorderAlbumsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default);

    Task<bool> ShouldAcceptIncomingAlbumMetaAsync(string albumId, long remoteLastUpdatedTicks, CancellationToken ct = default);
    Task<bool> ShouldAcceptIncomingImageAsync(string albumId, GalleryImage image, CancellationToken ct = default);
    Task<bool> TryApplyRemoteAlbumDeleteAsync(string albumId, long deletedAtTicks, CancellationToken ct = default);
    Task<bool> TryApplyRemoteImageDeleteAsync(string albumId, string imageId, long deletedAtTicks, CancellationToken ct = default);

    /// <summary>Apply remote album meta (title/protection). Does not touch image bytes.</summary>
    Task ApplyRemoteAlbumMetaAsync(
        string albumId,
        string title,
        bool? isPasswordProtected,
        long? protectionChangedTicks,
        CancellationToken ct = default);
}
