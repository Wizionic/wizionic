namespace ChatfishApp.Client.Services;

/// <summary>
/// Per-model Ollama settings persisted in browser localStorage (inside <see cref="WasmKeyStore.OllamaConfig"/>).
/// </summary>
public sealed class OllamaModelSettings
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public bool SupportsTools { get; set; } = true;
    public bool SupportsVision { get; set; }
    /// <summary>
    /// When true, non-vision models route image uploads through this model first to produce text descriptions.
    /// Only one model should be marked as the vision proxy at a time.
    /// </summary>
    public bool IsVisionProxy { get; set; }
    public int ContextSize { get; set; }
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
        UserOverrideTools = UserOverrideTools,
        UserOverrideVision = UserOverrideVision,
        UserOverrideContext = UserOverrideContext
    };
}