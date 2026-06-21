namespace ChatfishApp.Maui.Services;

/// <summary>
/// Saves auth cookies to SQLite after each HTTP response (e.g. verify-code Set-Cookie).
/// </summary>
public sealed class PersistCookiesHandler : DelegatingHandler
{
    private readonly MauiAuthCookieStore _store;

    public PersistCookiesHandler(MauiAuthCookieStore store) => _store = store;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        await _store.PersistCookiesAsync();
        return response;
    }
}