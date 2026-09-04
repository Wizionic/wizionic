using App.Core.Support;

namespace App.Maui.Services;

/// <summary>Desktop: OS handler (mail client / default browser). Not the in-app WebView.</summary>
public sealed class MauiExternalUriOpener : IExternalUriOpener
{
    public async Task OpenAsync(string uri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return;

        await Launcher.Default.OpenAsync(new Uri(uri));
    }
}
