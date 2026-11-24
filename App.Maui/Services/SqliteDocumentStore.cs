using App.Core.Auth;
using App.Core.Storage;

namespace App.Maui.Services;

/// <summary>
/// MAUI Document Library store: SQLite meta + encrypted raw bytes (BIN1: prefix),
/// mirroring SqliteGalleryStore's binary image path.
/// </summary>
public sealed class SqliteDocumentStore : IDocumentStore
{
    private const string BinV1Prefix = "BIN1:";

    private readonly SqliteHistoryDatabase _db;
    private readonly IAuthService _auth;
    private readonly MauiCryptoService _mauiCrypto;

    public SqliteDocumentStore(
        SqliteHistoryDatabase db,
        IAuthService auth,
        MauiCryptoService mauiCrypto)
    {
        _db = db;
        _auth = auth;
        _mauiCrypto = mauiCrypto;
    }

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private async Task<string?> GetKeyAsync()
    {
        try { return await _auth.GetOrCreateHistoryEncryptionKeyAsync(); }
        catch { return null; }
    }

    private static string MetaKey(string ns, string id) => ns + "d-wizionic-doc-" + id;
    private static string ContentKey(string ns, string id) => ns + "d-wizionic-doc-c-" + id;

    public async Task<List<LibraryDocument>> LoadIndexAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetDocumentMetasByNamespaceAsync(ns, ct);
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
        var encrypted = await _db.GetDocumentContentAsync(ContentKey(ns, documentId), ct);
        if (string.IsNullOrEmpty(encrypted))
            return null;

        var keyB64 = await GetKeyAsync();
        if (encrypted.StartsWith(BinV1Prefix, StringComparison.Ordinal))
        {
            var cipher = Convert.FromBase64String(encrypted[BinV1Prefix.Length..]);
            return string.IsNullOrEmpty(keyB64) ? cipher : _mauiCrypto.DecryptBytes(keyB64, cipher);
        }

        // Legacy / unencrypted fallback: raw base64 payload.
        try { return Convert.FromBase64String(encrypted); }
        catch { return null; }
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
        var keyB64 = await GetKeyAsync();

        byte[] cipher;
        if (!string.IsNullOrEmpty(keyB64))
            cipher = _mauiCrypto.EncryptBytes(keyB64, bytes);
        else
            cipher = bytes;
        var toStore = BinV1Prefix + Convert.ToBase64String(cipher);
        await _db.SetDocumentContentAsync(ContentKey(ns, id), toStore, ct);

        var now = DateTime.UtcNow;
        await _db.UpsertDocumentMetaAsync(new SqliteHistoryDatabase.DocumentMetaRow(
            MetaKey(ns, id),
            id,
            ns,
            name,
            contentType,
            bytes.Length,
            now.ToString("o"),
            source,
            conversationId), ct);

        return new LibraryDocument(id, name, contentType, bytes.Length, now, source, conversationId);
    }

    public async Task DeleteAsync(string documentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return;
        var ns = GetPrefix();
        await _db.DeleteDocumentMetaAsync(MetaKey(ns, documentId), ct);
        await _db.DeleteDocumentContentAsync(ContentKey(ns, documentId), ct);
    }

    public async Task<long> SumStoredBytesAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetDocumentMetasByNamespaceAsync(ns, ct);
        long sum = 0;
        foreach (var m in metas)
            if (m.Size > 0) sum += m.Size;
        return sum;
    }
}
