namespace ChatfishApp.Core.Storage;

/// <summary>
/// Hooks for note save/delete to trigger cross-device sync (WASM only for now).
/// </summary>
public interface INotesSyncBridge
{
    event Action? OnNotesChanged;
    void ScheduleAutoSyncNoteAfterLocalSave(string noteId, string title);
    void ScheduleAutoSyncNoteDeleteAfterLocalDelete(string noteId, DateTime deletedAt);
}