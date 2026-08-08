using System.Collections.Concurrent;
using App.Core.Tools;

namespace App.Shared.Services.Tools;

/// <summary>
/// Singleton buffer: recent generated images keyed by conversation + generation id.
/// Caps memory so long chats do not retain unbounded base64.
/// </summary>
public sealed class ConversationMediaBuffer : IConversationMediaBuffer
{
    private const int MaxPerConversation = 8;
    private const int MaxConversations = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedList<BufferedImage>> _byConvo =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _convoOrder = new();

    public string AddImage(
        string conversationId,
        string base64,
        string contentType,
        string? name = null,
        string? source = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            conversationId = "_default";
        if (string.IsNullOrWhiteSpace(base64))
            throw new ArgumentException("Image base64 is required.", nameof(base64));

        var id = "img_" + Guid.NewGuid().ToString("N")[..6];
        var ct = string.IsNullOrWhiteSpace(contentType) ? "image/png" : contentType;
        var entry = new BufferedImage(id, base64, ct, name, source, DateTime.UtcNow);

        lock (_gate)
        {
            if (!_byConvo.TryGetValue(conversationId, out var list))
            {
                list = new LinkedList<BufferedImage>();
                _byConvo[conversationId] = list;
                _convoOrder.Enqueue(conversationId);
                while (_convoOrder.Count > MaxConversations)
                {
                    var old = _convoOrder.Dequeue();
                    _byConvo.Remove(old);
                }
            }

            list.AddFirst(entry);
            while (list.Count > MaxPerConversation)
                list.RemoveLast();
        }

        return id;
    }

    public bool TryGetImage(string conversationId, string generationId, out BufferedImage? image)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(generationId))
            return false;

        lock (_gate)
        {
            if (!_byConvo.TryGetValue(conversationId, out var list))
                return false;
            foreach (var item in list)
            {
                if (string.Equals(item.GenerationId, generationId, StringComparison.OrdinalIgnoreCase))
                {
                    image = item;
                    return true;
                }
            }
        }

        return false;
    }

    public bool TryGetLatestImage(string conversationId, out BufferedImage? image)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(conversationId))
            conversationId = "_default";

        lock (_gate)
        {
            if (!_byConvo.TryGetValue(conversationId, out var list) || list.Count == 0)
                return false;
            image = list.First!.Value;
            return true;
        }
    }

    public IReadOnlyList<BufferedImageSummary> ListRecent(string conversationId, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            conversationId = "_default";
        max = Math.Clamp(max, 1, MaxPerConversation);

        lock (_gate)
        {
            if (!_byConvo.TryGetValue(conversationId, out var list))
                return Array.Empty<BufferedImageSummary>();

            return list.Take(max).Select(i => new BufferedImageSummary(
                i.GenerationId,
                i.Name,
                i.ContentType,
                i.Source,
                i.CreatedUtc,
                (long)(i.Base64.Length * 0.75))).ToList();
        }
    }

    public void ClearConversation(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        lock (_gate)
            _byConvo.Remove(conversationId);
    }
}
