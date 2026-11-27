namespace App.Core.Storage;

/// <summary>
/// Per-model settings for a user-added OpenAI-compatible cloud provider.
/// </summary>
public sealed class CloudModelSettings
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public bool SupportsTools { get; set; } = true;
    public bool SupportsVision { get; set; }
    public int ContextSize { get; set; }

    /// <summary>Canonical or provider model id from models.dev, when matched.</summary>
    public string? ModelsDevId { get; set; }
    /// <summary>Where capability/context metadata came from (provider, models.dev, manual).</summary>
    public string? CatalogSource { get; set; }
    public string? Family { get; set; }

    /// <summary>Vendor/model output token ceiling from models.dev. 0 = unknown.</summary>
    public int MaxOutputTokens { get; set; }
    public bool SupportsReasoning { get; set; }
    public string? KnowledgeCutoff { get; set; }
    public string? ReleaseDate { get; set; }

    public List<string> InputModalities { get; set; } = new();
    public List<string> OutputModalities { get; set; } = new();

    /// <summary>USD per 1M tokens, from models.dev. Display only.</summary>
    public double? CostInputPer1M { get; set; }
    public double? CostOutputPer1M { get; set; }
    public double? CostCacheReadPer1M { get; set; }

    public bool IsImage { get; set; }
    public bool IsEdit { get; set; }
    public bool IsTts { get; set; }
    public bool IsTranscription { get; set; }
    public bool IsEmbeddings { get; set; }
    public bool IsReranking { get; set; }

    public bool IsAdded { get; set; }

    public bool UserOverrideTools { get; set; }
    public bool UserOverrideVision { get; set; }
    public bool UserOverrideContext { get; set; }
    public bool UserOverrideImage { get; set; }
    public bool UserOverrideEdit { get; set; }

    /// <summary>Chat picker: LLM only — not pure image / TTS / STT / embeddings.</summary>
    public bool IsChatEligible =>
        !(IsImage || IsEdit || IsTts || IsTranscription || IsEmbeddings || IsReranking);

    public CloudModelSettings Clone() => new()
    {
        Name = Name,
        Label = Label,
        SupportsTools = SupportsTools,
        SupportsVision = SupportsVision,
        ContextSize = ContextSize,
        ModelsDevId = ModelsDevId,
        CatalogSource = CatalogSource,
        Family = Family,
        MaxOutputTokens = MaxOutputTokens,
        SupportsReasoning = SupportsReasoning,
        KnowledgeCutoff = KnowledgeCutoff,
        ReleaseDate = ReleaseDate,
        InputModalities = InputModalities?.ToList() ?? new List<string>(),
        OutputModalities = OutputModalities?.ToList() ?? new List<string>(),
        CostInputPer1M = CostInputPer1M,
        CostOutputPer1M = CostOutputPer1M,
        CostCacheReadPer1M = CostCacheReadPer1M,
        IsImage = IsImage,
        IsEdit = IsEdit,
        IsTts = IsTts,
        IsTranscription = IsTranscription,
        IsEmbeddings = IsEmbeddings,
        IsReranking = IsReranking,
        IsAdded = IsAdded,
        UserOverrideTools = UserOverrideTools,
        UserOverrideVision = UserOverrideVision,
        UserOverrideContext = UserOverrideContext,
        UserOverrideImage = UserOverrideImage,
        UserOverrideEdit = UserOverrideEdit
    };
}

public sealed class CloudTtsVoice
{
    public string VoiceId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// User-added OpenAI-compatible cloud provider. Stored only on the device.
/// </summary>
public sealed class CloudProviderConfig
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public List<CloudModelSettings> Models { get; set; } = new();

    /// <summary>Provider id in models.dev (for metadata and model matching).</summary>
    public string? ModelsDevProviderId { get; set; }
    public string? LogoUrl { get; set; }
    public string? DocUrl { get; set; }
    public DateTime? CatalogUpdatedAt { get; set; }
    public string? DefaultImageModel { get; set; }
    public string? DefaultEditModel { get; set; }
    public string? DefaultTtsModel { get; set; }
    public string? DefaultSttModel { get; set; }
    public string? DefaultVoice { get; set; }

    /// <summary>Vendor listed TTS/STT models usable via <c>/audio/speech</c> and <c>/audio/transcriptions</c>.</summary>
    public bool HasOpenAiAudio { get; set; }

    /// <summary>Vendor answered <c>GET /tts/voices</c> or otherwise supports xAI-style <c>POST /tts</c>.</summary>
    public bool HasXaiTts { get; set; }

    /// <summary>Vendor supports xAI-style <c>POST /stt</c> (set when language-models or tts/voices succeed).</summary>
    public bool HasXaiStt { get; set; }

    /// <summary>Vendor answered <c>GET /image-generation-models</c> (xAI-style image API, including JSON edits).</summary>
    public bool HasXaiImageApi { get; set; }

    public List<CloudTtsVoice> Voices { get; set; } = new();

    public CloudProviderConfig Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Models = Models.Select(m => m.Clone()).ToList(),
        ModelsDevProviderId = ModelsDevProviderId,
        LogoUrl = LogoUrl,
        DocUrl = DocUrl,
        CatalogUpdatedAt = CatalogUpdatedAt,
        DefaultImageModel = DefaultImageModel,
        DefaultEditModel = DefaultEditModel,
        DefaultTtsModel = DefaultTtsModel,
        DefaultSttModel = DefaultSttModel,
        DefaultVoice = DefaultVoice,
        HasOpenAiAudio = HasOpenAiAudio,
        HasXaiTts = HasXaiTts,
        HasXaiStt = HasXaiStt,
        HasXaiImageApi = HasXaiImageApi,
        Voices = Voices.Select(v => new CloudTtsVoice { VoiceId = v.VoiceId, Name = v.Name }).ToList()
    };
}
