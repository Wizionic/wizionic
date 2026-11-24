using App.Core.Auth;
using App.Core.Storage;
using Microsoft.JSInterop;

namespace App.Client.Services;

/// <summary>
/// WASM Document Library store backed by IndexedDB (documentMetas + documentContents).
/// Meta is cleartext JSON for fast listing; bytes are encrypted raw (bin-v1) —
/// never multi-MB JSON blobs. Mirrors WasmGalleryStore's binary path.
/// </summary>
public sealed class WasmDocumentStore : IDocumentStore
{
    private readonly IJSRuntime _js;
    private readonly IAuthService _auth;

    public WasmDocumentStore(IJSRuntime js, IAuthService auth)
    {
        _js = js;
        _auth = auth;
    }

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private sealed record StoredDocumentMeta(
        string Key,
        string Id,
        string Namespace,
        string Name,
        string ContentType,
        long Size,
        string LastUpdated,
        string? Source,
        string? ConversationId);

    private static string MetaKey(string ns, string id) => ns + "d-wizionic-doc-" + id;
    private static string ContentKey(string ns, string id) => ns + "d-wizionic-doc-c-" + id;

    public async Task<List<LibraryDocument>> LoadIndexAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredDocumentMeta>>("idbGetDocumentMetasByNamespace", ns);
        var list = new List<LibraryDocument>();
        foreach (var m in metas)
        {
            if (!DateTime.TryParse(m.LastUpdated, out var last))
                last = DateTime.UtcNow;
            list.Add(new LibraryDocument(
                Id: m.Id,
                Name: m.Name,
                ContentType: m.ContentType,
                Size: m.Size,
                CreatedUtc: last,
                Source: m.Source,
                ConversationId: m.ConversationId));
        }
        return list.OrderByDescending(d => d.CreatedUtc).ToList();
    }

    public async Task<byte[]?> LoadBytesAsync(string documentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return null;
        var ns = GetPrefix();
        var keyB64 = await _auth.GetOrCreateHistoryEncryptionKeyAsync();
        var b64 = await _js.InvokeAsync<string?>("documentLoadBase64FromIdb", ContentKey(ns, documentId), keyB64);
        if (string.IsNullOrWhiteSpace(b64))
            return null;
        try
        {
            return Convert.FromBase64String(b64);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LibraryDocument> SaveAsync(
        string name,
        string contentType,
        byte[] bytes,
        string? source = null,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        if (bytes == null || bytes.Length == 0)
            throw new ArgumentException("Document bytes are required.", nameof(bytes));
        if (string.IsNullOrWhiteSpace(name))
            name = "document";

        var ns = GetPrefix();
        var id = Guid.NewGuid().ToString("N")[..12];
        var keyB64 = await _auth.GetOrCreateHistoryEncryptionKeyAsync();

        // Encrypt + store raw bytes entirely in JS (no multi-MB base64 round-trip).
        await _js.InvokeVoidAsync("documentStoreBytes", ContentKey(ns, id), keyB64, bytes);

        var now = DateTime.UtcNow;
        var meta = new StoredDocumentMeta(
            MetaKey(ns, id),
            id,
            ns,
            name,
            contentType,
            bytes.Length,
            now.ToString("o"),
            source,
            conversationId);
        await _js.InvokeVoidAsync("idbPutDocumentMeta", meta);

        return new LibraryDocument(id, name, contentType, bytes.Length, now, source, conversationId);
    }

    public async Task DeleteAsync(string documentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return;
        var ns = GetPrefix();
        await _js.InvokeVoidAsync("idbDeleteDocumentByKey", MetaKey(ns, documentId), ContentKey(ns, documentId));
    }

    public async Task<long> SumStoredBytesAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredDocumentMeta>>("idbGetDocumentMetasByNamespace", ns);
        long sum = 0;
        foreach (var m in metas)
            if (m.Size > 0) sum += m.Size;
        return sum;
    }
}
