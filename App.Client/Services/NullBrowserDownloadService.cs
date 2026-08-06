using App.Core.Browser;

namespace App.Client.Services;

/// <summary>No-op downloads for WASM (embedded browser is MAUI-only).</summary>
public sealed class NullBrowserDownloadService : IBrowserDownloadService
{
    public IReadOnlyList<BrowserDownloadItem> Downloads { get; } = Array.Empty<BrowserDownloadItem>();
    public bool IsAnyInProgress => false;
    public event Action? Changed;

    public BrowserDownloadItem Begin(string url, string filePath, string? fileName = null) =>
        new() { Url = url, FilePath = filePath, FileName = fileName ?? "download" };

    public void Update(string id, Action<BrowserDownloadItem> mutator) { }
    public void Complete(string id, string? finalPath = null) { }
    public void Fail(string id, string? message = null) { }
    public void Cancel(string id) { }
    public Task OpenAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task ShowInFolderAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public void RemoveFromList(string id) { }
    public void ClearCompleted() { }
}
