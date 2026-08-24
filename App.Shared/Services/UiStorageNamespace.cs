using App.Core.Auth;
using App.Core.Storage;
using Microsoft.JSInterop;

namespace App.Shared.Services;

/// <summary>
/// Applies the current auth storage prefix to theme / nav-layout / sidebar JS modules
/// so those UI preferences stay isolated per user on the same browser/device.
/// </summary>
public static class UiStorageNamespace
{
    /// <summary>
    /// Sets JS localStorage key prefixes for theme, nav layout, and sidebar collapse.
    /// Safe to call repeatedly; no-ops modules that are not present (e.g. MAUI without sidebar JS).
    /// </summary>
    public static async Task ApplyAsync(IJSRuntime js, IAuthService? auth, CancellationToken ct = default)
    {
        var prefix = auth is null
            ? StorageNamespace.GuestPrefix
            : StorageNamespace.GetPrefix(auth);

        try
        {
            await js.InvokeVoidAsync("appTheme.setStoragePrefix", ct, prefix);
        }
        catch
        {
            // Theme module may not be loaded yet during early render.
        }

        try
        {
            await js.InvokeVoidAsync("appNavLayout.setStoragePrefix", ct, prefix);
        }
        catch
        {
        }

        try
        {
            await js.InvokeVoidAsync("appSidebar.setStoragePrefix", ct, prefix);
        }
        catch
        {
            // Sidebar interop is WASM-oriented; MAUI may not expose it.
        }

        try
        {
            await js.InvokeVoidAsync("appNotesUi.setStoragePrefix", ct, prefix);
        }
        catch
        {
            // Notes UI module may not be loaded yet during early render.
        }
    }
}
