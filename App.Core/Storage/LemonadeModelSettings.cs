namespace App.Core.Storage;

/// <summary>
/// Per-model Lemonade settings persisted in local storage (browser or SQLite).
/// Labels/recipe come from Lemonade <c>GET /v1/models</c> and drive modality UI.
/// </summary>
public sealed class LemonadeModelSettings
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public bool SupportsTools { get; set; } = true;
    public bool SupportsVision { get; set; }
    public bool IsVisionProxy { get; set; }
    public int ContextSize { get; set; }

    /// <summary>Deployment: text-to-image generation.</summary>
    public bool IsImage { get; set; }

    /// <summary>Deployment: image editing.</summary>
    public bool IsEdit { get; set; }

    /// <summary>Deployment: text-to-speech.</summary>
    public bool IsTts { get; set; }

    /// <summary>Deployment: speech-to-text transcription.</summary>
    public bool IsTranscription { get; set; }

    /// <summary>Deployment: embeddings (not a chat model).</summary>
    public bool IsEmbeddings { get; set; }

    /// <summary>Deployment: reranking (not a chat model).</summary>
    public bool IsReranking { get; set; }

    /// <summary>Omni collection (<c>recipe: collection.omni</c>) — chat with server-side multimodal tools.</summary>
    public bool IsOmniCollection { get; set; }

    public string? Recipe { get; set; }
    public List<string> Labels { get; set; } = new();
    public double? SizeGb { get; set; }

    /// <summary>From Lemonade <c>image_defaults.steps</c>.</summary>
    public int? DefaultSteps { get; set; }

    /// <summary>From Lemonade <c>image_defaults.cfg_scale</c>.</summary>
    public double? DefaultCfgScale { get; set; }

    /// <summary>From Lemonade <c>image_defaults.width</c>.</summary>
    public int? DefaultWidth { get; set; }

    /// <summary>From Lemonade <c>image_defaults.height</c>.</summary>
    public int? DefaultHeight { get; set; }

    public bool UserOverrideTools { get; set; }
    public bool UserOverrideVision { get; set; }
    public bool UserOverrideContext { get; set; }

    /// <summary>
    /// True when this model should appear in the chat model picker
    /// (LLM or Omni collection; not pure image/TTS/STT/embeddings).
    /// </summary>
    public bool IsChatEligible =>
        IsOmniCollection ||
        !(IsImage || IsEdit || IsTts || IsTranscription || IsEmbeddings || IsReranking);

    public LemonadeModelSettings Clone() => new()
    {
        Name = Name,
        Label = Label,
        SupportsTools = SupportsTools,
        SupportsVision = SupportsVision,
        IsVisionProxy = IsVisionProxy,
        ContextSize = ContextSize,
        IsImage = IsImage,
        IsEdit = IsEdit,
        IsTts = IsTts,
        IsTranscription = IsTranscription,
        IsEmbeddings = IsEmbeddings,
        IsReranking = IsReranking,
        IsOmniCollection = IsOmniCollection,
        Recipe = Recipe,
        Labels = Labels?.ToList() ?? new List<string>(),
        SizeGb = SizeGb,
        DefaultSteps = DefaultSteps,
        DefaultCfgScale = DefaultCfgScale,
        DefaultWidth = DefaultWidth,
        DefaultHeight = DefaultHeight,
        UserOverrideTools = UserOverrideTools,
        UserOverrideVision = UserOverrideVision,
        UserOverrideContext = UserOverrideContext
    };
}

/// <summary>
/// Lemonade server configuration stored only on the client (browser / MAUI).
/// Independent of <see cref="OllamaConfig"/>.
/// </summary>
public record LemonadeConfig(
    string BaseUrl = "http://localhost:13305",
    string? ApiKey = null,
    List<string>? Models = null,
    Dictionary<string, LemonadeModelSettings>? ModelSettings = null,
    string? DefaultImageModel = null,
    string? DefaultEditModel = null,
    string? DefaultTtsModel = null,
    string? DefaultSttModel = null,
    string? DefaultVoice = null);
