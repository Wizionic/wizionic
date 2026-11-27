using System.Text.Json.Serialization;

namespace App.Core.ModelsDev;

/// <summary>
/// One provider entry from models.dev (<c>api.json</c>), keyed by provider id.
/// </summary>
public sealed class ModelsDevProvider
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>OpenAI-compatible base URL when models.dev publishes one.</summary>
    [JsonPropertyName("api")]
    public string? Api { get; set; }

    /// <summary>AI SDK package name; <c>@ai-sdk/openai-compatible</c> marks gateways.</summary>
    [JsonPropertyName("npm")]
    public string? Npm { get; set; }

    [JsonPropertyName("doc")]
    public string? Doc { get; set; }

    [JsonPropertyName("env")]
    public List<string> Env { get; set; } = new();

    [JsonPropertyName("models")]
    public Dictionary<string, ModelsDevModel> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    [JsonIgnore]
    public int ModelCount => Models?.Count ?? 0;

    [JsonIgnore]
    public bool IsOpenAiCompatible =>
        !string.IsNullOrWhiteSpace(Api) ||
        string.Equals(Npm, "@ai-sdk/openai-compatible", StringComparison.OrdinalIgnoreCase);

    public ModelsDevProviderSummary ToSummary() => new()
    {
        Id = Id,
        Name = DisplayName,
        Api = Api,
        Npm = Npm,
        Doc = Doc,
        Env = Env?.ToList() ?? new List<string>(),
        ModelCount = Models?.Count ?? 0,
        IsOpenAiCompatible = IsOpenAiCompatible
    };

    public ModelsDevProvider CloneWithoutDescription() => new()
    {
        Id = Id,
        Name = Name,
        Api = Api,
        Npm = Npm,
        Doc = Doc,
        Env = Env?.ToList() ?? new List<string>(),
        Models = (Models ?? new Dictionary<string, ModelsDevModel>(StringComparer.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase)
    };
}

/// <summary>Lightweight provider row used by the Cloud providers template picker.</summary>
public sealed class ModelsDevProviderSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("api")]
    public string? Api { get; set; }

    [JsonPropertyName("npm")]
    public string? Npm { get; set; }

    [JsonPropertyName("doc")]
    public string? Doc { get; set; }

    [JsonPropertyName("env")]
    public List<string> Env { get; set; } = new();

    [JsonPropertyName("modelCount")]
    public int ModelCount { get; set; }

    [JsonPropertyName("openAiCompatible")]
    public bool IsOpenAiCompatible { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

/// <summary>One model as served by a provider (a value inside <see cref="ModelsDevProvider.Models"/>).</summary>
public sealed class ModelsDevModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>chat, image, video, speech, transcription, embedding, reranking, realtime, decision.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("canonical_model_id")]
    public string? CanonicalModelId { get; set; }

    [JsonPropertyName("attachment")]
    public bool? Attachment { get; set; }

    [JsonPropertyName("reasoning")]
    public bool? Reasoning { get; set; }

    [JsonPropertyName("tool_call")]
    public bool? ToolCall { get; set; }

    [JsonPropertyName("structured_output")]
    public bool? StructuredOutput { get; set; }

    [JsonPropertyName("temperature")]
    public bool? Temperature { get; set; }

    [JsonPropertyName("open_weights")]
    public bool? OpenWeights { get; set; }

    [JsonPropertyName("knowledge")]
    public string? Knowledge { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("last_updated")]
    public string? LastUpdated { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("modalities")]
    public ModelsDevModalities? Modalities { get; set; }

    [JsonPropertyName("limit")]
    public ModelsDevLimit? Limit { get; set; }

    [JsonPropertyName("cost")]
    public ModelsDevCost? Cost { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public ModelsDevModel Clone() => new()
    {
        Id = Id,
        Name = Name,
        Type = Type,
        Family = Family,
        CanonicalModelId = CanonicalModelId,
        Attachment = Attachment,
        Reasoning = Reasoning,
        ToolCall = ToolCall,
        StructuredOutput = StructuredOutput,
        Temperature = Temperature,
        OpenWeights = OpenWeights,
        Knowledge = Knowledge,
        ReleaseDate = ReleaseDate,
        LastUpdated = LastUpdated,
        Status = Status,
        Modalities = Modalities?.Clone(),
        Limit = Limit?.Clone(),
        Cost = Cost?.Clone()
    };
}

public sealed class ModelsDevModalities
{
    [JsonPropertyName("input")]
    public List<string> Input { get; set; } = new();

    [JsonPropertyName("output")]
    public List<string> Output { get; set; } = new();

    public ModelsDevModalities Clone() => new()
    {
        Input = Input.ToList(),
        Output = Output.ToList()
    };
}

public sealed class ModelsDevLimit
{
    [JsonPropertyName("context")]
    public long? Context { get; set; }

    [JsonPropertyName("input")]
    public long? Input { get; set; }

    [JsonPropertyName("output")]
    public long? Output { get; set; }

    public ModelsDevLimit Clone() => new()
    {
        Context = Context,
        Input = Input,
        Output = Output
    };
}

public sealed class ModelsDevCost
{
    /// <summary>USD per 1M input tokens.</summary>
    [JsonPropertyName("input")]
    public double? Input { get; set; }

    /// <summary>USD per 1M output tokens.</summary>
    [JsonPropertyName("output")]
    public double? Output { get; set; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; set; }

    public ModelsDevCost Clone() => new()
    {
        Input = Input,
        Output = Output,
        CacheRead = CacheRead
    };
}

/// <summary>
/// Provider-agnostic facts about a model, resolved either from a provider entry,
/// a canonical models.dev entry, or both.
/// </summary>
public sealed record ModelCatalogMetadata(
    string? ModelsDevId,
    int ContextSize = 0,
    int MaxOutputTokens = 0,
    bool? SupportsTools = null,
    bool? SupportsVision = null,
    bool? SupportsReasoning = null,
    bool? IsImage = null,
    bool? IsEdit = null,
    bool? IsTts = null,
    bool? IsTranscription = null,
    bool? IsEmbeddings = null,
    bool? IsReranking = null,
    bool? IsVideo = null,
    bool? IsRealtime = null,
    string? KnowledgeCutoff = null,
    string? ReleaseDate = null,
    string? Family = null,
    double? CostInputPer1M = null,
    double? CostOutputPer1M = null,
    double? CostCacheReadPer1M = null,
    IReadOnlyList<string>? InputModalities = null,
    IReadOnlyList<string>? OutputModalities = null);
