namespace ChatfishApp.Core.UI;

public enum NavLayoutMode
{
    Top,
    Left
}

public interface INavLayoutState
{
    NavLayoutMode Mode { get; }
    event Action? OnChanged;
    Task InitializeAsync(Microsoft.JSInterop.IJSRuntime js, CancellationToken ct = default);
    Task SetModeAsync(NavLayoutMode mode, Microsoft.JSInterop.IJSRuntime js, CancellationToken ct = default);
    Task SyncFromStorageAndApplyAsync(Microsoft.JSInterop.IJSRuntime js, CancellationToken ct = default);
}