using Microsoft.JSInterop;

namespace ChatfishApp.Shared.Services;

internal static class ThemeInterop
{
    private const string JsPrefix = "chatfishTheme.";

    public static async Task<ThemePreferences> GetSavedAsync(IJSRuntime js, CancellationToken ct = default)
    {
        var colorScheme = await js.InvokeAsync<string?>($"{JsPrefix}getColorScheme", ct);
        var theme = await js.InvokeAsync<string?>($"{JsPrefix}getTheme", ct);
        return new ThemePreferences
        {
            ColorScheme = colorScheme ?? ThemeService.DefaultColorScheme,
            Theme = theme ?? ThemeService.DefaultThemeId
        };
    }

    public static ValueTask SaveAsync(IJSRuntime js, string colorScheme, string theme, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}save", ct, colorScheme, theme);

    public static ValueTask ApplyAsync(IJSRuntime js, string colorScheme, string theme, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}apply", ct, colorScheme, theme);

    public static ValueTask ApplySavedAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}applySaved", ct);

    public static ValueTask InitPersistentHooksAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}initPersistentHooks", ct);

    public static ValueTask InitSystemListenerAsync<T>(IJSRuntime js, DotNetObjectReference<T> dotnetRef, CancellationToken ct = default)
        where T : class =>
        js.InvokeVoidAsync($"{JsPrefix}initSystemListener", ct, dotnetRef);

    public static ValueTask DisposeSystemListenerAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}disposeSystemListener", ct);
}

internal sealed class ThemePreferences
{
    public string ColorScheme { get; set; } = ThemeService.DefaultColorScheme;
    public string Theme { get; set; } = ThemeService.DefaultThemeId;
}