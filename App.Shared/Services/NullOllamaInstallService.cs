using App.Core.Ollama;
using App.Core.Storage;

namespace App.Shared.Services;

public sealed class NullOllamaInstallService : IOllamaInstallService
{
    public static readonly NullOllamaInstallService Instance = new();

    private NullOllamaInstallService() { }

    public bool IsSupported => false;
    public bool IsInstalled => false;
    public string GetInstalledStatusDescription() => "Not available on this platform.";
    public string DefaultBaseUrl => "http://localhost:11434";

    public IReadOnlyList<OllamaInstallModelChoice> AvailableInstallModels { get; } =
        Array.Empty<OllamaInstallModelChoice>();

    public Task<OllamaInstallResult> InstallServerAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OllamaInstallResult.Fail("Ollama install is only available on Windows desktop."));

    public Task<OllamaInstallResult> PullModelsAsync(
        IEnumerable<string> modelIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OllamaInstallResult.Fail("Not supported."));

    public Task<OllamaInstallResult> ConfigureAppAsync(
        HttpClient http,
        IKeyStore keyStore,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OllamaInstallResult.Fail("Not supported."));
}
