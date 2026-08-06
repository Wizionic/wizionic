using App.Core.Lemonade;
using App.Core.Storage;

namespace App.Shared.Services;

public sealed class NullLemonadeInstallService : ILemonadeInstallService
{
    public static readonly NullLemonadeInstallService Instance = new();

    private NullLemonadeInstallService() { }

    public bool IsSupported => false;
    public bool IsInstalled => false;
    public string GetInstalledStatusDescription() => "Not available on this platform.";
    public string DefaultBaseUrl => "http://localhost:13305";

    public IReadOnlyList<LemonadeInstallModelChoice> AvailableInstallModels { get; } =
        Array.Empty<LemonadeInstallModelChoice>();

    public Task<LemonadeInstallResult> InstallServerAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LemonadeInstallResult.Fail("Lemonade install is only available on Windows desktop."));

    public Task<LemonadeInstallResult> PullModelsAsync(
        IEnumerable<string> modelIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LemonadeInstallResult.Fail("Not supported."));

    public Task<LemonadeInstallResult> ConfigureAppAsync(
        HttpClient http,
        IKeyStore keyStore,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LemonadeInstallResult.Fail("Not supported."));
}
