using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Storage;
using Microsoft.JSInterop;

namespace ChatfishApp.Shared.Services;

/// <summary>
/// Applies the current auth storage prefix to theme / nav-layout / sidebar JS modules
/// so those UI preferences stay isolated per user (or guest) on the same browser/device.
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
            await js.InvokeVoidAsync("chatfishTheme.setStoragePrefix", ct, prefix);
        }
        catch
        {
            // Theme module may not be loaded yet during early render.
        }

        try
        {
            await js.InvokeVoidAsync("chatfishNavLayout.setStoragePrefix", ct, prefix);
        }
        catch
        {
        }

        try
        {
            await js.InvokeVoidAsync("chatfishSidebar.setStoragePrefix", ct, prefix);
        }
        catch
        {
            // Sidebar interop is WASM-oriented; MAUI may not expose it.
        }
    }
}
