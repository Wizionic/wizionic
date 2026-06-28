using Microsoft.JSInterop;

namespace ChatfishApp.Shared.Services;

public sealed class ThemeService
{
    public const string DefaultThemeId = "default";
    public const string DefaultColorScheme = "system";

    private static readonly ThemeOption[] ThemeCatalog =
    [
        new("default", "Chatfish"),
        new("dark", "Dark"),
        new("dracula", "Dracula"),
        new("nord", "Nord"),
        new("solarized-light", "Solarized Light"),
    ];

    private static readonly ColorSchemeOption[] ColorSchemeCatalog =
    [
        new("system", "System"),
        new("light", "Light"),
        new("dark", "Dark"),
    ];

    private string _colorScheme = DefaultColorScheme;
    private string _theme = DefaultThemeId;

    public event Action? OnChanged;

    public string ColorScheme => _colorScheme;
    public string Theme => _theme;

    public IReadOnlyList<ThemeOption> AvailableThemes => ThemeCatalog;
    public IReadOnlyList<ColorSchemeOption> AvailableColorSchemes => ColorSchemeCatalog;

    public string GetThemeDisplayName(string themeId)
    {
        foreach (var theme in ThemeCatalog)
        {
            if (theme.Id == themeId)
                return theme.DisplayName;
        }

        return themeId;
    }

    public string GetColorSchemeDisplayName(string schemeId)
    {
        foreach (var scheme in ColorSchemeCatalog)
        {
            if (scheme.Id == schemeId)
                return scheme.DisplayName;
        }

        return schemeId;
    }

    /// <summary>
    /// Loads preferences from browser storage and applies them to the document.
    /// Safe to call on every page mount (per-page WASM roots recreate this service).
    /// </summary>
    public async Task SyncFromStorageAndApplyAsync(IJSRuntime js, CancellationToken ct = default)
    {
        var saved = await ThemeInterop.GetSavedAsync(js, ct);
        var scheme = NormalizeColorScheme(saved.ColorScheme);
        var theme = NormalizeTheme(saved.Theme);
        var changed = scheme != _colorScheme || theme != _theme;

        _colorScheme = scheme;
        _theme = theme;
        await ThemeInterop.ApplyAsync(js, _colorScheme, _theme, ct);

        if (changed)
            OnChanged?.Invoke();
    }

    public async Task InitializeAsync<T>(IJSRuntime js, DotNetObjectReference<T>? systemListener, CancellationToken ct = default)
        where T : class
    {
        await ThemeInterop.InitPersistentHooksAsync(js, ct);
        await SyncFromStorageAndApplyAsync(js, ct);

        if (systemListener != null)
            await ThemeInterop.InitSystemListenerAsync(js, systemListener, ct);
    }

    public async Task SetColorSchemeAsync(string scheme, IJSRuntime js, CancellationToken ct = default)
    {
        _colorScheme = NormalizeColorScheme(scheme);
        await PersistAndApplyAsync(js, ct);
    }

    public async Task SetThemeAsync(string theme, IJSRuntime js, CancellationToken ct = default)
    {
        _theme = NormalizeTheme(theme);
        await PersistAndApplyAsync(js, ct);
    }

    public async Task HandleSystemColorSchemeChangedAsync(IJSRuntime js, CancellationToken ct = default)
    {
        if (_colorScheme != "system")
            return;

        await ThemeInterop.ApplyAsync(js, _colorScheme, _theme, ct);
        OnChanged?.Invoke();
    }

    private async Task PersistAndApplyAsync(IJSRuntime js, CancellationToken ct = default)
    {
        await ThemeInterop.SaveAsync(js, _colorScheme, _theme, ct);
        await ThemeInterop.ApplyAsync(js, _colorScheme, _theme, ct);
        OnChanged?.Invoke();
    }

    private static string NormalizeColorScheme(string? scheme) =>
        ColorSchemeCatalog.Any(s => s.Id == scheme) ? scheme! : DefaultColorScheme;

    private static string NormalizeTheme(string? theme) =>
        ThemeCatalog.Any(t => t.Id == theme) ? theme! : DefaultThemeId;
}

public readonly record struct ThemeOption(string Id, string DisplayName);

public readonly record struct ColorSchemeOption(string Id, string DisplayName);