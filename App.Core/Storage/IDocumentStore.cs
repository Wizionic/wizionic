namespace App.Core.Storage;

/// <summary>
/// One saved document in the device-local Document Library (e.g. a generated PDF).
/// Meta (name/size/dates) is cleartext for fast listing; bytes are encrypted at rest.
/// </summary>
public record LibraryDocument(
    string Id,
    string Name,
    string ContentType,
    long Size,
    DateTime CreatedUtc,
    string? Source = null,
    string? ConversationId = null);

/// <summary>
/// Device-local library of saved documents (PDFs). Mirrors the gallery's local-first
/// storage pattern; cross-device sync is a follow-up.
/// </summary>
public interface IDocumentStore
{
    /// <summary>Meta-only listing, newest first.</summary>
    Task<List<LibraryDocument>> LoadIndexAsync(CancellationToken ct = default);

    /// <summary>Decrypted bytes for one document (null when missing).</summary>
    Task<byte[]?> LoadBytesAsync(string documentId, CancellationToken ct = default);

    /// <summary>Encrypt + persist; returns the meta record.</summary>
    Task<LibraryDocument> SaveAsync(
        string name,
        string contentType,
        byte[] bytes,
        string? source = null,
        string? conversationId = null,
        CancellationToken ct = default);

    Task DeleteAsync(string documentId, CancellationToken ct = default);

    /// <summary>Sum of stored sizes (meta only — never decrypts). Used for quota.</summary>
    Task<long> SumStoredBytesAsync(CancellationToken ct = default);
}
