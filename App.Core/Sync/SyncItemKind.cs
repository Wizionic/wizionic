namespace App.Core.Sync;

/// <summary>Categories of items transferred over the WebRTC sync DataChannel.</summary>
public enum SyncItemKind
{
    Conversation = 0,
    Note = 1,
    Bookmark = 2,
    BookmarkFolder = 3,
    SidebarApp = 4,
    /// <summary>Settings bundle; <see cref="SyncQueueItem.ItemId"/> is the category id.</summary>
    Settings = 5,
    /// <summary>Gallery album meta only (title, protection, image id set — no bytes).</summary>
    Album = 6,
    /// <summary>Single gallery image create/update/delete.</summary>
    AlbumImage = 7
}
