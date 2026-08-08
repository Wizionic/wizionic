using App.Core.Storage;
using Microsoft.JSInterop;

namespace App.Shared.Services;

/// <summary>
/// Scans conversations for image attachments and exposes them as virtual gallery images.
/// Image id format: <c>chat|{convoId}|{messageItemId}|{attachmentIndex}</c>.
/// </summary>
public sealed class ChatMediaLibrary : IChatMediaLibrary
{
    private const string IdPrefix = "chat|";
    private const long SmallImageFallbackBytes = 180 * 1024;

    private readonly IConversationStore _conversations;
    private readonly IGalleryStore _gallery;
    private readonly IJSRuntime _js;

    public ChatMediaLibrary(
        IConversationStore conversations,
        IGalleryStore gallery,
        IJSRuntime js)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _gallery = gallery ?? throw new ArgumentNullException(nameof(gallery));
        _js = js ?? throw new ArgumentNullException(nameof(js));
    }

    public async Task<List<GalleryImage>> LoadThumbsAsync(CancellationToken ct = default)
    {
        var list = new List<GalleryImage>();
        await foreach (var item in EnumerateChatImagesAsync(ct))
        {
            var size = item.Att.Size > 0 ? item.Att.Size : (long)(item.Att.DataBase64.Length * 0.75);
            var (thumb, w, h) = await PrepareThumbAsync(item.Att.DataBase64, item.Att.ContentType, size);
            list.Add(new GalleryImage(
                Id: item.ImageId,
                Name: string.IsNullOrWhiteSpace(item.Att.Name) ? "chat-image.png" : item.Att.Name,
                ContentType: item.Att.ContentType,
                DataBase64: "", // grid must not hold multi-MB strings for every tile
                Size: size,
                ThumbnailBase64: thumb,
                Width: w,
                Height: h,
                Timestamp: item.Timestamp,
                ModifiedAt: item.Timestamp));
        }

        // Newest first
        return list
            .OrderByDescending(i => i.Timestamp ?? DateTime.MinValue)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<(string? Thumb, int? W, int? H)> PrepareThumbAsync(
        string base64, string contentType, long approxBytes)
    {
        try
        {
            var prep = await _js.InvokeAsync<PrepResult?>("galleryPrepareImage", base64, contentType, 400);
            if (prep != null && !string.IsNullOrEmpty(prep.thumbnailBase64))
            {
                int? w = prep.width > 0 ? prep.width : null;
                int? h = prep.height > 0 ? prep.height : null;
                return (prep.thumbnailBase64, w, h);
            }
        }
        catch { /* fall through */ }

        // Small images: use full payload as tile source so the grid is not blank.
        if (approxBytes > 0 && approxBytes <= SmallImageFallbackBytes)
            return (base64, null, null);
        return (base64, null, null); // still show something for local My Media
    }

    private sealed class PrepResult
    {
        public int width { get; set; }
        public int height { get; set; }
        public string? thumbnailBase64 { get; set; }
    }

    public async Task<GalleryImage?> LoadImageAsync(string imageId, CancellationToken ct = default)
    {
        if (!TryParseId(imageId, out var convoId, out var itemId, out var attIndex))
            return null;

        var msgs = await _conversations.LoadConversationAsync(convoId, ct);
        var msg = msgs.FirstOrDefault(m =>
            m.DeletedAt is null
            && string.Equals(m.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        if (msg?.Attachments == null)
            return null;

        var images = msg.Attachments
            .Where(a => IsImageAttachment(a))
            .ToList();
        if (attIndex < 0 || attIndex >= images.Count)
            return null;

        var att = images[attIndex];
        var ts = msg.ModifiedAt ?? msg.Timestamp ?? DateTime.UtcNow;
        return new GalleryImage(
            Id: imageId,
            Name: string.IsNullOrWhiteSpace(att.Name) ? "chat-image.png" : att.Name,
            ContentType: att.ContentType,
            DataBase64: att.DataBase64,
            Size: att.Size > 0 ? att.Size : (long)(att.DataBase64.Length * 0.75),
            ThumbnailBase64: null,
            Timestamp: ts,
            ModifiedAt: ts);
    }

    public async Task DeletePointerAsync(string imageId, CancellationToken ct = default)
    {
        if (!TryParseId(imageId, out var convoId, out var itemId, out var attIndex))
            return;

        var msgs = await _conversations.LoadConversationAsync(convoId, ct);
        var msgIndex = msgs.FindIndex(m =>
            m.DeletedAt is null
            && string.Equals(m.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        if (msgIndex < 0)
            return;

        var msg = msgs[msgIndex];
        if (msg.Attachments == null || msg.Attachments.Count == 0)
            return;

        // Remove the Nth *image* attachment (same ordering as LoadThumbs)
        var imageOrdinal = -1;
        for (var i = 0; i < msg.Attachments.Count; i++)
        {
            if (!IsImageAttachment(msg.Attachments[i]))
                continue;
            imageOrdinal++;
            if (imageOrdinal != attIndex)
                continue;

            msg.Attachments.RemoveAt(i);
            var now = DateTime.UtcNow;
            msgs[msgIndex] = msg with
            {
                Attachments = msg.Attachments.Count > 0 ? msg.Attachments : null,
                ModifiedAt = now
            };
            await _conversations.SaveConversationAsync(convoId, msgs, ct);
            var index = await _conversations.LoadIndexAsync(ct);
            await _conversations.UpdateIndexAfterSaveAsync(convoId, msgs, index, ct);
            return;
        }
    }

    public async Task<string> CopyToAlbumAsync(string imageId, string targetAlbumId, CancellationToken ct = default)
    {
        if (GalleryConstants.IsMyMediaAlbum(targetAlbumId))
            throw new InvalidOperationException("Cannot add images into My Media (virtual album).");

        var img = await LoadImageAsync(imageId, ct);
        if (img == null || string.IsNullOrWhiteSpace(img.DataBase64))
            throw new InvalidOperationException("Chat image not found.");

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(NormalizeBase64(img.DataBase64));
        }
        catch
        {
            throw new InvalidOperationException("Invalid image data.");
        }

        var newId = Guid.NewGuid().ToString("N");
        await _gallery.UpsertImageFromRawBytesAsync(
            targetAlbumId,
            newId,
            img.Name,
            img.ContentType,
            raw,
            ct);
        return newId;
    }

    public async Task<int> CountImagesAsync(CancellationToken ct = default)
    {
        var n = 0;
        await foreach (var _ in EnumerateChatImagesAsync(ct))
            n++;
        return n;
    }

    private async IAsyncEnumerable<ChatImageRef> EnumerateChatImagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var index = await _conversations.LoadIndexAsync(ct);
        foreach (var convo in index.OrderByDescending(c => c.LastUpdated))
        {
            ct.ThrowIfCancellationRequested();
            List<ChatMessage> msgs;
            try
            {
                msgs = await _conversations.LoadConversationAsync(convo.Id, ct);
            }
            catch
            {
                continue;
            }

            foreach (var msg in msgs.Where(m => m.DeletedAt is null && m.Attachments is { Count: > 0 }))
            {
                var itemId = string.IsNullOrWhiteSpace(msg.ItemId)
                    ? StableFallbackItemId(msg)
                    : msg.ItemId!;
                var attIndex = 0;
                foreach (var att in msg.Attachments!)
                {
                    if (!IsImageAttachment(att))
                        continue;
                    var imageId = BuildId(convo.Id, itemId, attIndex);
                    yield return new ChatImageRef(
                        imageId,
                        convo.Id,
                        itemId,
                        attIndex,
                        att,
                        msg.ModifiedAt ?? msg.Timestamp ?? convo.LastUpdated);
                    attIndex++;
                }
            }
        }
    }

    private static bool IsImageAttachment(Attachment a) =>
        a.ContentType != null
        && a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(a.DataBase64);

    public static string BuildId(string convoId, string itemId, int attIndex) =>
        $"{IdPrefix}{convoId}|{itemId}|{attIndex}";

    public static bool TryParseId(string imageId, out string convoId, out string itemId, out int attIndex)
    {
        convoId = "";
        itemId = "";
        attIndex = -1;
        if (string.IsNullOrWhiteSpace(imageId) || !imageId.StartsWith(IdPrefix, StringComparison.Ordinal))
            return false;

        var rest = imageId[IdPrefix.Length..];
        var parts = rest.Split('|');
        if (parts.Length != 3)
            return false;
        if (!int.TryParse(parts[2], out attIndex) || attIndex < 0)
            return false;
        convoId = parts[0];
        itemId = parts[1];
        return !string.IsNullOrWhiteSpace(convoId) && !string.IsNullOrWhiteSpace(itemId);
    }

    private static string StableFallbackItemId(ChatMessage msg)
    {
        // Legacy messages without ItemId — stable-ish within a conversation load.
        var t = (msg.Timestamp ?? DateTime.MinValue).Ticks;
        var h = (msg.Content ?? "").GetHashCode();
        return $"legacy-{t:x}-{h:x8}";
    }

    private static string NormalizeBase64(string b64)
    {
        var s = b64.Trim();
        var comma = s.IndexOf(',');
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            s = s[(comma + 1)..];
        return s;
    }

    private sealed record ChatImageRef(
        string ImageId,
        string ConvoId,
        string ItemId,
        int AttIndex,
        Attachment Att,
        DateTime Timestamp);
}
