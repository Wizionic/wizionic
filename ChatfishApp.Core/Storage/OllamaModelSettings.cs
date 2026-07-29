namespace ChatfishApp.Core.Storage;

/// <summary>
/// Per-model Ollama settings persisted in local storage (browser or SQLite).
/// </summary>
public sealed class OllamaModelSettings
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public bool SupportsTools { get; set; } = true;
    public bool SupportsVision { get; set; }
    public bool IsVisionProxy { get; set; }
    public int ContextSize { get; set; }

    /// <summary>On-disk size from Ollama <c>/api/tags</c> (bytes). 0 when unknown.</summary>
    public long SizeBytes { get; set; }

    public bool UserOverrideTools { get; set; }
    public bool UserOverrideVision { get; set; }
    public bool UserOverrideContext { get; set; }

    public OllamaModelSettings Clone() => new()
    {
        Name = Name,
        Label = Label,
        SupportsTools = SupportsTools,
        SupportsVision = SupportsVision,
        IsVisionProxy = IsVisionProxy,
        ContextSize = ContextSize,
        SizeBytes = SizeBytes,
        UserOverrideTools = UserOverrideTools,
        UserOverrideVision = UserOverrideVision,
        UserOverrideContext = UserOverrideContext
    };
}

public record OllamaConfig(
    string BaseUrl = "http://localhost:11434",
    List<string>? Models = null,
    Dictionary<string, OllamaModelSettings>? ModelSettings = null);

public record CustomMcpConnector(string Name, string ServerUrl);