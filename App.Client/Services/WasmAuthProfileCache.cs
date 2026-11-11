using System.Text.Json;
using App.Core.Auth;
using Microsoft.JSInterop;

namespace App.Client.Services;

/// <summary>
/// Same-origin localStorage copy of the last successful sign-in.
/// Not user-prefixed: the user id is what this record provides at startup.
/// </summary>
public sealed class WasmAuthProfileCache : IAuthProfileCache
{
    private const string StorageKey = "wizionic-auth-profile";

    private readonly IJSRuntime _js;

    public WasmAuthProfileCache(IJSRuntime js) => _js = js;

    public Task SaveAsync(AuthProfileSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot);
        return _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json).AsTask();
    }

    public async Task<AuthProfileSnapshot?> LoadAsync()
    {
        var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        return JsonSerializer.Deserialize<AuthProfileSnapshot>(json);
    }

    public Task ClearAsync() =>
        _js.InvokeVoidAsync("localStorage.removeItem", StorageKey).AsTask();
}
