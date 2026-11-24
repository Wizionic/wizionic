using System.Collections.Concurrent;
using App.Core.Tools;

namespace App.Shared.Services.Tools;

/// <summary>
/// Singleton buffer: recent generated images/documents keyed by conversation + id.
/// Caps memory so long chats do not retain unbounded base64.
/// </summary>
public sealed class ConversationMediaBuffer : IConversationMediaBuffer
{
    private const int MaxPerConversation = 8;
    private const int MaxConversations = 32;

    private readonly MediaRing _images = new("img_", "image/png");
    private readonly MediaRing _documents = new("doc_", "application/octet-stream");

    public string AddImage(
        string conversationId,
        string base64,
        string contentType,
        string? name = null,
        string? source = null) =>
        _images.Add(conversationId, base64, contentType, name, source);

    public bool TryGetImage(string conversationId, string generationId, out BufferedImage? image)
    {
        image = null;
        if (!_images.TryGet(conversationId, generationId, out var e))
            return false;
        image = new BufferedImage(e.Id, e.Base64, e.ContentType, e.Name, e.Source, e.CreatedUtc);
        return true;
    }

    public bool TryGetLatestImage(string conversationId, out BufferedImage? image)
    {
        image = null;
        if (!_images.TryGetLatest(conversationId, out var e))
            return false;
        image = new BufferedImage(e.Id, e.Base64, e.ContentType, e.Name, e.Source, e.CreatedUtc);
        return true;
    }

    public IReadOnlyList<BufferedImageSummary> ListRecent(string conversationId, int max = 8) =>
        _images.ListRecent(conversationId, max)
            .Select(i => new BufferedImageSummary(
                i.Id, i.Name, i.ContentType, i.Source, i.CreatedUtc, (long)(i.Base64.Length * 0.75)))
            .ToList();

    public string AddDocument(
        string conversationId,
        string base64,
        string contentType,
        string? name = null,
        string? source = null) =>
        _documents.Add(conversationId, base64, contentType, name, source);

    public bool TryGetDocument(string conversationId, string documentId, out BufferedDocument? document)
    {
        document = null;
        if (!_documents.TryGet(conversationId, documentId, out var e))
            return false;
        document = new BufferedDocument(e.Id, e.Base64, e.ContentType, e.Name, e.Source, e.CreatedUtc);
        return true;
    }

    public IReadOnlyList<BufferedDocumentSummary> ListRecentDocuments(string conversationId, int max = 8) =>
        _documents.ListRecent(conversationId, max)
            .Select(d => new BufferedDocumentSummary(
                d.Id, d.Name, d.ContentType, d.Source, d.CreatedUtc, (long)(d.Base64.Length * 0.75)))
            .ToList();

    public void ClearConversation(string conversationId)
    {
        _images.ClearConversation(conversationId);
        _documents.ClearConversation(conversationId);
    }

    /// <summary>Per-conversation capped ring of base64 media entries (newest first).</summary>
    private sealed class MediaRing
    {
        private readonly string _idPrefix;
        private readonly string _defaultContentType;
        private readonly object _gate = new();
        private readonly Dictionary<string, LinkedList<Entry>> _byConvo =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _convoOrder = new();

        public MediaRing(string idPrefix, string defaultContentType)
        {
            _idPrefix = idPrefix;
            _defaultContentType = defaultContentType;
        }

        public sealed record Entry(
            string Id,
            string Base64,
            string ContentType,
            string? Name,
            string? Source,
            DateTime CreatedUtc);

        public string Add(
            string conversationId,
            string base64,
            string contentType,
            string? name,
            string? source)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
                conversationId = "_default";
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("Media base64 is required.", nameof(base64));

            var id = _idPrefix + Guid.NewGuid().ToString("N")[..6];
            var ct = string.IsNullOrWhiteSpace(contentType) ? _defaultContentType : contentType;
            var entry = new Entry(id, base64, ct, name, source, DateTime.UtcNow);

            lock (_gate)
            {
                if (!_byConvo.TryGetValue(conversationId, out var list))
                {
                    list = new LinkedList<Entry>();
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

        public bool TryGet(string conversationId, string id, out Entry entry)
        {
            entry = null!;
            if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(id))
                return false;

            lock (_gate)
            {
                if (!_byConvo.TryGetValue(conversationId, out var list))
                    return false;
                foreach (var item in list)
                {
                    if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        entry = item;
                        return true;
                    }
                }
            }

            return false;
        }

        public bool TryGetLatest(string conversationId, out Entry entry)
        {
            entry = null!;
            if (string.IsNullOrWhiteSpace(conversationId))
                conversationId = "_default";

            lock (_gate)
            {
                if (!_byConvo.TryGetValue(conversationId, out var list) || list.Count == 0)
                    return false;
                entry = list.First!.Value;
                return true;
            }
        }

        public IReadOnlyList<Entry> ListRecent(string conversationId, int max)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
                conversationId = "_default";
            max = Math.Clamp(max, 1, MaxPerConversation);

            lock (_gate)
            {
                if (!_byConvo.TryGetValue(conversationId, out var list))
                    return Array.Empty<Entry>();
                return list.Take(max).ToList();
            }
        }

        public void ClearConversation(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            lock (_gate)
                _byConvo.Remove(conversationId);
        }
    }
}