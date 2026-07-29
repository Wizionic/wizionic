namespace ChatfishApp.Core.Ollama;

public interface IOllamaInstallService
{
    bool IsSupported { get; }

    /// <summary>True when ollama CLI/app or API on :11434 is present.</summary>
    bool IsInstalled { get; }

    string GetInstalledStatusDescription();

    string DefaultBaseUrl { get; }

    IReadOnlyList<OllamaInstallModelChoice> AvailableInstallModels { get; }

    Task<OllamaInstallResult> InstallServerAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<OllamaInstallResult> PullModelsAsync(
        IEnumerable<string> modelIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<OllamaInstallResult> ConfigureChatfishAsync(
        HttpClient http,
        ChatfishApp.Core.Storage.IKeyStore keyStore,
        CancellationToken cancellationToken = default);
}

public sealed class OllamaInstallModelChoice
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public bool DefaultSelected { get; init; }
}

public sealed class OllamaInstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";

    public static OllamaInstallResult Ok(string message) => new() { Success = true, Message = message };
    public static OllamaInstallResult Fail(string message) => new() { Success = false, Message = message };
}
