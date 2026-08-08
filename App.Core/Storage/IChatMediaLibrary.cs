namespace App.Core.Storage;

/// <summary>
/// Virtual "My Media" library: image attachments across chat conversations (pointers into chat storage).
/// </summary>
public interface IChatMediaLibrary
{
    /// <summary>Thumbs/meta for the grid — may include full base64 as ThumbnailBase64 when no separate thumb.</summary>
    Task<List<GalleryImage>> LoadThumbsAsync(CancellationToken ct = default);

    /// <summary>Full image for lightbox / download / move. Id from <see cref="LoadThumbsAsync"/>.</summary>
    Task<GalleryImage?> LoadImageAsync(string imageId, CancellationToken ct = default);

    /// <summary>Remove the attachment from its source chat message (pointer disappears from My Media).</summary>
    Task DeletePointerAsync(string imageId, CancellationToken ct = default);

    /// <summary>
    /// Copy image bytes into a real gallery album. Chat attachment is left in place
    /// (My Media still lists it until the chat image is deleted).
    /// Returns the new gallery image id.
    /// </summary>
    Task<string> CopyToAlbumAsync(string imageId, string targetAlbumId, CancellationToken ct = default);

    /// <summary>Count of chat images (for album list date/activity; optional).</summary>
    Task<int> CountImagesAsync(CancellationToken ct = default);
}
