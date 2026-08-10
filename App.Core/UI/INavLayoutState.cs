namespace App.Core.UI;

public enum NavLayoutMode
{
    Top,
    Left
}

/// <summary>Optional primary app icons that can be hidden from the nav bar.</summary>
public enum NavPrimaryIcon
{
    Browser,
    Notes,
    Gallery,
    Calendar
}

public interface INavLayoutState
{
    NavLayoutMode Mode { get; }

    bool ShowBrowser { get; }
    bool ShowNotes { get; }
    bool ShowGallery { get; }
    bool ShowCalendar { get; }

    /// <summary>When false, settings-group icons are hidden; account/profile stays visible.</summary>
    bool SecondaryExpanded { get; }

    event Action? OnChanged;

    Task InitializeAsync(Microsoft.JSInterop.IJSRuntime js, CancellationToken ct = default);
    Task SetModeAsync(NavLayoutMode mode, Microsoft.JSInterop.IJSRuntime js, CancellationToken ct = default);
    Task SetPrimaryIconVisibleAsync(NavPrimaryIcon icon, bool visible, Microsoft.JSInterop.IJSRuntime js, CancellationToken ct = default);
    Task SetSecondaryExpandedAsync(bool expanded, Microsoft.JSInterop.IJSRuntime js, CancellationToken ct = default);
    Task SyncFromStorageAndApplyAsync(Microsoft.JSInterop.IJSRuntime js, CancellationToken ct = default);
}
