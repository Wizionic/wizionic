using ChatfishApp.Core.Browser;

namespace ChatfishApp.Client.Services;

public sealed class NullPwaDetector : IPwaDetector
{
    public PwaManifest? CurrentManifest => null;
    public bool IsCurrentPagePinned => false;
    public event Action? Changed;

    public Task DetectFromPageAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Clear() { }
}