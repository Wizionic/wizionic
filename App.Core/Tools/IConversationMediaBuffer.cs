namespace App.Core.Tools;

/// <summary>
/// Per-conversation ring of AI-generated images for tool follow-ups
/// (e.g. save_to_gallery without re-passing multi-MB base64).
/// </summary>
public interface IConversationMediaBuffer
{
    /// <summary>Store image; returns a short id (e.g. img_a3f2) for tools / model reference.</summary>
    string AddImage(
        string conversationId,
        string base64,
        string contentType,
        string? name = null,
        string? source = null);

    bool TryGetImage(string conversationId, string generationId, out BufferedImage? image);

    /// <summary>Most recent image for this conversation.</summary>
    bool TryGetLatestImage(string conversationId, out BufferedImage? image);

    /// <summary>Newest first, capped — no base64 (for list / disambiguation).</summary>
    IReadOnlyList<BufferedImageSummary> ListRecent(string conversationId, int max = 8);

    void ClearConversation(string conversationId);
}

public sealed record BufferedImage(
    string GenerationId,
    string Base64,
    string ContentType,
    string? Name,
    string? Source,
    DateTime CreatedUtc);

public sealed record BufferedImageSummary(
    string GenerationId,
    string? Name,
    string ContentType,
    string? Source,
    DateTime CreatedUtc,
    long ApproxBytes);
