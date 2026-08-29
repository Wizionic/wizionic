namespace App.Core.Auth;

/// <summary>
/// Sends <see cref="ClientDeviceKeys.IdHeader"/> only to this app's origin.
/// Third-party hosts (Lemonade, Ollama, user-keyed cloud) must not see it: browsers
/// CORS-preflight custom headers, and those servers do not allow them.
/// </summary>
public sealed class ClientDeviceHeaderHandler : DelegatingHandler
{
    private readonly Uri _appOrigin;
    private readonly IClientDeviceId? _deviceId;

    public ClientDeviceHeaderHandler(Uri appOrigin, IClientDeviceId? deviceId)
    {
        _appOrigin = appOrigin ?? throw new ArgumentNullException(nameof(appOrigin));
        _deviceId = deviceId;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var dest = request.RequestUri;
        if (dest is null || IsAppOrigin(dest))
            await AttachAsync(request);
        else
            Strip(request);

        return await base.SendAsync(request, cancellationToken);
    }

    private bool IsAppOrigin(Uri dest)
    {
        // Relative URLs are resolved against HttpClient.BaseAddress (the app origin).
        if (!dest.IsAbsoluteUri)
            return true;

        return Uri.Compare(
                   _appOrigin,
                   dest,
                   UriComponents.SchemeAndServer,
                   UriFormat.Unescaped,
                   StringComparison.OrdinalIgnoreCase) == 0;
    }

    private async Task AttachAsync(HttpRequestMessage request)
    {
        if (_deviceId is null)
            return;

        try
        {
            var id = await _deviceId.GetOrCreateAsync();
            if (string.IsNullOrWhiteSpace(id))
                return;

            request.Headers.Remove(ClientDeviceKeys.IdHeader);
            request.Headers.TryAddWithoutValidation(ClientDeviceKeys.IdHeader, id);

            var name = await _deviceId.GetNameAsync();
            request.Headers.Remove(ClientDeviceKeys.NameHeader);
            var encodedName = ClientDeviceKeys.EncodeNameHeader(name);
            if (!string.IsNullOrWhiteSpace(encodedName))
                request.Headers.TryAddWithoutValidation(ClientDeviceKeys.NameHeader, encodedName);
        }
        catch
        {
            // JS not ready / storage unavailable: omit rather than fail the request.
        }
    }

    private static void Strip(HttpRequestMessage request)
    {
        request.Headers.Remove(ClientDeviceKeys.IdHeader);
        request.Headers.Remove(ClientDeviceKeys.NameHeader);
    }
}
