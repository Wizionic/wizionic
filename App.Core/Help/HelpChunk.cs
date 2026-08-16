namespace App.Core.Help;

public sealed class HelpChunk
{
    public int Id { get; init; }
    public string TopicId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Anchor { get; init; }
    public string Text { get; init; } = "";
}

public sealed class HelpSearchHit
{
    public HelpChunk Chunk { get; init; } = new();
    public float Score { get; init; }
}

public sealed class HelpIndexStatus
{
    public bool Ready { get; init; }
    public int ChunkCount { get; init; }
    public bool HasVectors { get; init; }
    public string? CatalogHash { get; init; }
    public string? EmbedModelId { get; init; }
    public int Dimensions { get; init; }
    public DateTimeOffset? BuiltAtUtc { get; init; }
    public string? Error { get; init; }
}

public sealed class HelpAskResult
{
    public string AnswerMarkdown { get; init; } = "";
    public IReadOnlyList<HelpSearchHit> Citations { get; init; } = Array.Empty<HelpSearchHit>();
    public string Retrieval { get; init; } = "keyword";
    public string? Error { get; init; }
}
