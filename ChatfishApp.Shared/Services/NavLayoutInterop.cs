using ChatfishApp.Core.UI;
using Microsoft.JSInterop;

namespace ChatfishApp.Shared.Services;

internal static class NavLayoutInterop
{
    private const string JsPrefix = "chatfishNavLayout.";

    public static ValueTask<string> GetSavedModeAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeAsync<string>($"{JsPrefix}getMode", ct);

    public static ValueTask SaveModeAsync(IJSRuntime js, NavLayoutMode mode, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}saveMode", ct, ToStorageValue(mode));

    public static ValueTask ApplyAsync(IJSRuntime js, NavLayoutMode mode, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}apply", ct, ToStorageValue(mode));

    public static ValueTask ApplyEarlyAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}applyEarly", ct);

    private static string ToStorageValue(NavLayoutMode mode) =>
        mode == NavLayoutMode.Left ? "left" : "top";

    public static NavLayoutMode ParseMode(string? value) =>
        string.Equals(value, "left", StringComparison.OrdinalIgnoreCase)
            ? NavLayoutMode.Left
            : NavLayoutMode.Top;
}