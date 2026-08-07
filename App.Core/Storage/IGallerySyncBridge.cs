namespace App.Core.Storage;

/// <summary>
/// Hooks for album/image save/delete to trigger cross-device sync, and UI refresh when remote gallery changes.
/// </summary>
public interface IGallerySyncBridge
{
    event Action? OnGalleryChanged;

    void ScheduleAutoSyncAlbumMetaAfterLocalSave(string albumId, string title);
    void ScheduleAutoSyncAlbumDeleteAfterLocalDelete(string albumId, DateTime deletedAt);

    void ScheduleAutoSyncAlbumImageAfterLocalSave(string albumId, string imageId);
    void ScheduleAutoSyncAlbumImageDeleteAfterLocalDelete(string albumId, string imageId, DateTime deletedAt);
}
