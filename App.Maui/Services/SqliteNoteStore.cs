using App.Core.Auth;
using App.Core.Storage;

namespace App.Maui.Services;

public sealed class SqliteNoteStore(IAuthService auth, ICryptoService crypto, SqliteHistoryDatabase db, INoteAudioStore audio) : TransactionalNoteStore(auth, crypto)
{
    internal static NoteHeader Header(SqliteHistoryDatabase.NoteMetaRow m) => new(m.StorageKey, m.Id, m.Namespace, m.Title, m.LastUpdated,
        m.SyncEnabled, m.ContentFingerprint, m.DeletedAt, m.IsPasswordProtected, m.SortOrder, m.ProtectionChangedTicks, m.TitleChangedTicks);
    protected override async Task<List<NoteHeader>> HeadersAsync(string ns, CancellationToken ct) =>
        (await db.GetNoteMetasByNamespaceAsync(ns, ct)).Select(Header).ToList();
    protected override Task<NoteSnapshot> ReadAsync(string key, CancellationToken ct) => db.ReadNotebookAsync(key, ct);
    protected override Task<bool> ReplaceAsync(NoteSnapshot expected, NoteHeader meta, string? content, CancellationToken ct) =>
        db.ReplaceNotebookAsync(expected, meta, content, ct);
    protected override Task SetOrderAsync(string key, int order, CancellationToken ct) => db.SetNotebookOrderAsync(key, order, ct);
    protected override Task RemoveDraftRowAsync(string key, CancellationToken ct) => db.DeleteDraftRowAsync(key, ct);
    public override async Task<DateTime> DeleteNoteAsync(string id, CancellationToken ct = default)
    {
        var result = await base.DeleteNoteAsync(id, ct);
        await audio.DeleteByNotebookAsync(id, ct);
        return result;
    }
}
