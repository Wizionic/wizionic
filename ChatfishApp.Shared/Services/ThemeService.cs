using Microsoft.JSInterop;

namespace ChatfishApp.Shared.Services;

public sealed class ThemeService
{
    public const string DefaultThemeId = "system";

    private static readonly ThemeOption[] ThemeCatalog =
    [
        new("system",           "System default"),
        new("chatfish-light",   "Light"),
        new("chatfish-dark",    "Dark"),
        new("bella-purple",     "Bella Purple"),
        new("catppuccin-latte", "Catppuccin Latte"),
        new("dracula",          "Dracula"), 
        new("github-light",          "Github Light"), 
        new("nord",             "Nord"),
        new("solarized-light",  "Solarized Light"),
    ];

    private string _theme = DefaultThemeId;
    private bool _initialized;

    public event Action? OnChanged;

    public string Theme => _theme;
    public bool IsInitialized => _initialized;

    public IReadOnlyList<ThemeOption> AvailableThemes => ThemeCatalog;

    public async Task InitializeAsync<T>(IJSRuntime js, DotNetObjectReference<T>? systemListener, CancellationToken ct = default)
        where T : class
    {
        if (_initialized)
        {
            await ApplyAsync(js, ct);
            return;
        }

        var saved = await ThemeInterop.GetSavedThemeAsync(js, ct);
        _theme = NormalizeTheme(saved);
        _initialized = true;

        await ApplyAsync(js, ct);

        if (systemListener != null)
            await ThemeInterop.InitSystemListenerAsync(js, systemListener, ct);

        OnChanged?.Invoke();
    }

    public async Task SetThemeAsync(string theme, IJSRuntime js, CancellationToken ct = default)
    {
        var normalized = NormalizeTheme(theme);
        if (_theme == normalized) return;
        _theme = normalized;
        await ThemeInterop.SaveThemeAsync(js, _theme, ct);
        await ApplyAsync(js, ct);
        OnChanged?.Invoke();
    }

    public async Task ApplyAsync(IJSRuntime js, CancellationToken ct = default) =>
        await ThemeInterop.ApplyAsync(js, _theme, ct);

    public async Task HandleSystemColorSchemeChangedAsync(IJSRuntime js, CancellationToken ct = default)
    {
        if (_theme != "system") return;
        await ApplyAsync(js, ct);
        OnChanged?.Invoke();
    }

    public async Task SyncFromStorageAndApplyAsync(IJSRuntime js, CancellationToken ct = default)
    {
        var saved = await ThemeInterop.GetSavedThemeAsync(js, ct);
        var normalized = NormalizeTheme(saved);
        var changed = _theme != normalized;
        _theme = normalized;
        _initialized = true;
        await ApplyAsync(js, ct);
        if (changed) OnChanged?.Invoke();
    }

    private static string NormalizeTheme(string? theme) =>
        ThemeCatalog.Any(t => t.Id == theme) ? theme! : DefaultThemeId;
}

public readonly record struct ThemeOption(string Id, string DisplayName);