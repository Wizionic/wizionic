using App.Core.Auth;

namespace App.Maui.Services;

public sealed class MauiClientDeviceId : IClientDeviceId
{
    private readonly SqliteSettingsDatabase _db;
    private string? _id;
    private string? _name;

    public MauiClientDeviceId(SqliteSettingsDatabase db) => _db = db;

    public async Task<string> GetOrCreateAsync()
    {
        if (!string.IsNullOrWhiteSpace(_id))
            return _id;

        var id = await _db.GetStringAsync(ClientDeviceKeys.DeviceId);
        if (string.IsNullOrWhiteSpace(id))
        {
            id = Guid.NewGuid().ToString("N");
            await _db.SetStringAsync(ClientDeviceKeys.DeviceId, id);
        }
        _id = id;
        return _id;
    }

    public async Task<string?> GetNameAsync()
    {
        if (_name != null)
            return _name;
        _name = await _db.GetStringAsync(ClientDeviceKeys.DeviceName);
        return _name;
    }
}
