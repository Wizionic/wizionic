using App.Core.Auth;
using App.Core.Storage;
using App.Core.Sync;
using Microsoft.JSInterop;

namespace App.Client.Services;

/// <summary>
/// Browser localStorage for sync coordinator preferences (per-user prefix).
/// Device identity keys are stored outside this store.
/// </summary>
public sealed class JsSyncPreferencesStore : ISyncPreferencesStore
{
    private readonly IJSRuntime _js;
    private readonly IAuthService _auth;

    public JsSyncPreferencesStore(IJSRuntime js, IAuthService auth)
    {
        _js = js;
        _auth = auth;
    }

    private string Prefixed(string key) => StorageNamespace.PrefixedKey(_auth, key);

    public async Task<string?> GetStringAsync(string key, CancellationToken ct = default)
    {
        var nk = Prefixed(key);
        var value = await _js.InvokeAsync<string?>("localStorage.getItem", ct, nk);
        if (value != null)
            return value;

        var legacy = await _js.InvokeAsync<string?>("localStorage.getItem", ct, key);
        if (legacy != null)
        {
            await _js.InvokeVoidAsync("localStorage.setItem", ct, nk, legacy);
            return legacy;
        }

        return null;
    }

    public Task SetStringAsync(string key, string? value, CancellationToken ct = default)
    {
        var nk = Prefixed(key);
        if (value is null)
            return _js.InvokeVoidAsync("localStorage.removeItem", ct, nk).AsTask();
        return _js.InvokeVoidAsync("localStorage.setItem", ct, nk, value).AsTask();
    }

    public Task RemoveAsync(string key, CancellationToken ct = default) =>
        _js.InvokeVoidAsync("localStorage.removeItem", ct, Prefixed(key)).AsTask();
}
