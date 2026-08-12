using App.Core.Connectors;

namespace App.Maui.Services;

/// <summary>
/// Opens URLs in the <b>in-app</b> embedded browser (preferred for OAuth).
/// Falls back to the system browser only if the embedded WebView is unavailable.
/// </summary>
public sealed class MauiUriLauncher : IUriLauncher
{
    private readonly MauiOAuthInterceptor _oauth;

    public MauiUriLauncher(MauiOAuthInterceptor oauth) =>
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));

    public async Task OpenAsync(string uri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return;

        try
        {
            await _oauth.OpenInAppBrowserAsync(uri, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiUriLauncher] In-app browser failed: {ex.Message}; trying system browser");
            try
            {
                await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"[MauiUriLauncher] System browser failed: {ex2.Message}");
                await Launcher.Default.OpenAsync(uri);
            }
        }
    }
}
