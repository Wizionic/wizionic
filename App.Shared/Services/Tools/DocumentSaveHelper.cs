using App.Core.Storage;

namespace App.Shared.Services.Tools;

/// <summary>
/// Shared "save a generated document to the Document Library" flow used by the
/// Pdf tools and the chat Save-to-Library button: quota check + persist.
/// </summary>
public static class DocumentSaveHelper
{
    public static async Task<(bool Ok, string Message)> SaveDocumentAsync(
        IDocumentStore store,
        IStorageQuotaService? quota,
        byte[] raw,
        string contentType,
        string name,
        string? source = null,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        if (raw == null || raw.Length == 0)
            return (false, "document is empty.");

        if (quota != null)
        {
            var fits = await quota.CanAcceptBytesAsync(raw.Length, ct);
            if (!fits)
                return (false, "storage quota exceeded — free space in Settings → Devices & Sync, then try again.");
        }

        try
        {
            var doc = await store.SaveAsync(name, contentType, raw, source, conversationId, ct);
            var kb = doc.Size / 1024.0;
            return (true, $"Saved \"{doc.Name}\" to Documents ({kb:0.#} KB, id={doc.Id}).");
        }
        catch (Exception ex)
        {
            return (false, "save failed: " + ex.Message);
        }
    }
}
