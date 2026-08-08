namespace App.Core.Storage;

/// <summary>Reserved gallery ids and titles.</summary>
public static class GalleryConstants
{
    /// <summary>
    /// Virtual album that lists image attachments from chat history (pointers, not copies).
    /// Meta (password protection) may be stored under this id; image bytes live only in conversations.
    /// </summary>
    public const string MyMediaAlbumId = "my-media";

    public const string MyMediaAlbumTitle = "My Media";

    public static bool IsMyMediaAlbum(string? albumId) =>
        string.Equals(albumId, MyMediaAlbumId, StringComparison.OrdinalIgnoreCase);
}
