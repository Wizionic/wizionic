using App.Core.Auth;
using App.Core.Storage;
using Microsoft.JSInterop;

namespace App.Client.Services;

public sealed class WasmNoteStore(IAuthService auth, ICryptoService crypto, IJSRuntime js) : TransactionalNoteStore(auth, crypto)
{
    protected override Task<List<NoteHeader>> HeadersAsync(string ns, CancellationToken ct) =>
        js.InvokeAsync<List<NoteHeader>>("idbGetNoteMetasByNamespace", ct, ns).AsTask();
    protected override Task<NoteSnapshot> ReadAsync(string key, CancellationToken ct) =>
        js.InvokeAsync<NoteSnapshot>("idbReadNotebook", ct, key).AsTask();
    protected override Task<bool> ReplaceAsync(NoteSnapshot expected, NoteHeader meta, string? content, CancellationToken ct) =>
        js.InvokeAsync<bool>("idbReplaceNotebook", ct, expected, meta, content).AsTask();
    protected override Task SetOrderAsync(string key, int order, CancellationToken ct) =>
        js.InvokeVoidAsync("idbSetNotebookOrder", ct, key, order).AsTask();
    protected override Task RemoveDraftRowAsync(string key, CancellationToken ct) => js.InvokeVoidAsync("idbDeleteNoteByKey", ct, key).AsTask();
}
