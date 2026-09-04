using App.Core.Support;
using Microsoft.JSInterop;

namespace App.Shared.Services;

/// <summary>WASM / host: OS handler via location.href (mailto, https). Not the in-app browser.</summary>
public sealed class JsExternalUriOpener : IExternalUriOpener
{
    private readonly IJSRuntime _js;

    public JsExternalUriOpener(IJSRuntime js) => _js = js;

    public async Task OpenAsync(string uri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return;

        await _js.InvokeVoidAsync("openExternalUri", uri);
    }
}
