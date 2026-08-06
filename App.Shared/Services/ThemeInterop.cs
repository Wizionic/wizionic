using Microsoft.JSInterop;

namespace App.Shared.Services;

internal static class ThemeInterop
{
    private const string JsPrefix = "appTheme.";

    public static ValueTask<string> GetSavedThemeAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeAsync<string>($"{JsPrefix}getTheme", ct);

    public static ValueTask SaveThemeAsync(IJSRuntime js, string theme, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}saveTheme", ct, theme);

    public static ValueTask ApplyAsync(IJSRuntime js, string theme, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}apply", ct, theme);

    public static ValueTask InitSystemListenerAsync<T>(IJSRuntime js, DotNetObjectReference<T> dotnetRef, CancellationToken ct = default)
        where T : class =>
        js.InvokeVoidAsync($"{JsPrefix}initSystemListener", ct, dotnetRef);

    public static ValueTask DisposeSystemListenerAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}disposeSystemListener", ct);

    public static ValueTask ApplySavedAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}applyEarly", ct);

    public static ValueTask InitPersistentHooksAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}initPersistentHooks", ct);

}