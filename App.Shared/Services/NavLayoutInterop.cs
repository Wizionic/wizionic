using System.Text.Json;
using App.Core.UI;
using Microsoft.JSInterop;

namespace App.Shared.Services;

internal static class NavLayoutInterop
{
    private const string JsPrefix = "appNavLayout.";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ValueTask<NavLayoutPrefsDto> GetPrefsAsync(IJSRuntime js, CancellationToken ct = default) =>
        js.InvokeAsync<NavLayoutPrefsDto>($"{JsPrefix}getPrefs", ct);

    public static ValueTask SavePrefsAsync(IJSRuntime js, NavLayoutPrefsDto prefs, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}savePrefs", ct, prefs);

    public static ValueTask ApplyAsync(IJSRuntime js, NavLayoutMode mode, CancellationToken ct = default) =>
        js.InvokeVoidAsync($"{JsPrefix}apply", ct, ToStorageMode(mode));

    public static string ToStorageMode(NavLayoutMode mode) =>
        mode == NavLayoutMode.Left ? "left" : "top";

    public static NavLayoutMode ParseMode(string? value) =>
        string.Equals(value, "left", StringComparison.OrdinalIgnoreCase)
            ? NavLayoutMode.Left
            : NavLayoutMode.Top;

    public static NavLayoutMode ParseModeFromPrefs(NavLayoutPrefsDto? prefs, NavLayoutMode defaultMode)
    {
        if (prefs is null || string.IsNullOrWhiteSpace(prefs.Mode))
            return defaultMode;
        return ParseMode(prefs.Mode);
    }

    public sealed class NavLayoutPrefsDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("mode")]
        public string Mode { get; set; } = "top";

        [System.Text.Json.Serialization.JsonPropertyName("showBrowser")]
        public bool ShowBrowser { get; set; } = true;

        [System.Text.Json.Serialization.JsonPropertyName("showNotes")]
        public bool ShowNotes { get; set; } = true;

        [System.Text.Json.Serialization.JsonPropertyName("showGallery")]
        public bool ShowGallery { get; set; } = true;

        [System.Text.Json.Serialization.JsonPropertyName("showCalendar")]
        public bool ShowCalendar { get; set; } = true;

        [System.Text.Json.Serialization.JsonPropertyName("secondaryExpanded")]
        public bool SecondaryExpanded { get; set; } = true;
    }
}
