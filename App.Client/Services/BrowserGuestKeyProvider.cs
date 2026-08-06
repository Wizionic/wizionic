using App.Core.Auth;
using Microsoft.JSInterop;

namespace App.Client.Services;

public class BrowserGuestKeyProvider : IGuestKeyProvider
{
    private readonly IJSRuntime _js;

    public BrowserGuestKeyProvider(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<string> GetOrCreateGuestKeyAsync()
    {
        return await _js.InvokeAsync<string>("idbEnsureGuestKey");
    }
}