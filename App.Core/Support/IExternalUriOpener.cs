namespace App.Core.Support;

/// <summary>
/// Opens a URI with the OS handler (mail client, browser). Not the in-app WebView.
/// </summary>
public interface IExternalUriOpener
{
    Task OpenAsync(string uri, CancellationToken ct = default);
}
