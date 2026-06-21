using ChatfishApp.Core.Sync;
using Microsoft.JSInterop;

namespace ChatfishApp.Client.Services;

/// <summary>Wraps browser localStorage for sync coordinator preferences.</summary>
public sealed class JsSyncPreferencesStore : ISyncPreferencesStore
{
    private readonly IJSRuntime _js;

    public JsSyncPreferencesStore(IJSRuntime js) => _js = js;

    public Task<string?> GetStringAsync(string key, CancellationToken ct = default) =>
        _js.InvokeAsync<string?>("localStorage.getItem", ct, key).AsTask();

    public Task SetStringAsync(string key, string? value, CancellationToken ct = default)
    {
        if (value is null)
            return _js.InvokeVoidAsync("localStorage.removeItem", ct, key).AsTask();
        return _js.InvokeVoidAsync("localStorage.setItem", ct, key, value).AsTask();
    }

    public Task RemoveAsync(string key, CancellationToken ct = default) =>
        _js.InvokeVoidAsync("localStorage.removeItem", ct, key).AsTask();
}