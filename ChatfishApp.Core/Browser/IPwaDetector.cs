namespace ChatfishApp.Core.Browser;

public interface IPwaDetector
{
    PwaManifest? CurrentManifest { get; }
    bool IsCurrentPagePinned { get; }

    event Action? Changed;

    Task DetectFromPageAsync(CancellationToken ct = default);
    void Clear();
}