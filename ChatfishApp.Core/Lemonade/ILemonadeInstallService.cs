namespace ChatfishApp.Core.Lemonade;

public interface ILemonadeInstallService
{
    bool IsSupported { get; }

    /// <summary>
    /// True when Lemonade is already on this PC (running process, API on :13305, CLI, or install folder).
    /// When true, <see cref="InstallServerAsync"/> must not re-download/reinstall the MSI.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>Short explanation of how Lemonade was detected (for setup UI).</summary>
    string GetInstalledStatusDescription();

    string DefaultBaseUrl { get; }

    IReadOnlyList<LemonadeInstallModelChoice> AvailableInstallModels { get; }

    Task<LemonadeInstallResult> InstallServerAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<LemonadeInstallResult> PullModelsAsync(
        IEnumerable<string> modelIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Set KeyStore base URL and refresh model list from the running server.</summary>
    Task<LemonadeInstallResult> ConfigureChatfishAsync(
        HttpClient http,
        ChatfishApp.Core.Storage.IKeyStore keyStore,
        CancellationToken cancellationToken = default);
}

public sealed class LemonadeInstallModelChoice
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public bool DefaultSelected { get; init; }
}

public sealed class LemonadeInstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";

    public static LemonadeInstallResult Ok(string message) => new() { Success = true, Message = message };
    public static LemonadeInstallResult Fail(string message) => new() { Success = false, Message = message };
}
