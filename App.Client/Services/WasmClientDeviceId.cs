using App.Core.Auth;
using Microsoft.JSInterop;

namespace App.Client.Services;

public sealed class WasmClientDeviceId : IClientDeviceId
{
    private readonly IJSRuntime _js;
    private string? _id;
    private string? _name;

    public WasmClientDeviceId(IJSRuntime js) => _js = js;

    public async Task<string> GetOrCreateAsync()
    {
        if (!string.IsNullOrWhiteSpace(_id))
            return _id;

        try
        {
            var id = await _js.InvokeAsync<string?>("localStorage.getItem", ClientDeviceKeys.DeviceId);
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N");
                await _js.InvokeVoidAsync("localStorage.setItem", ClientDeviceKeys.DeviceId, id);
            }
            _id = id;
            return _id;
        }
        catch
        {
            _id ??= Guid.NewGuid().ToString("N");
            return _id;
        }
    }

    public async Task<string?> GetNameAsync()
    {
        if (_name != null)
            return _name;
        try
        {
            _name = await _js.InvokeAsync<string?>("localStorage.getItem", ClientDeviceKeys.DeviceName);
        }
        catch
        {
            _name = null;
        }
        return _name;
    }
}
