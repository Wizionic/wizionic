using App.Core.Auth;
using App.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

public sealed class UserDeviceService
{
    private readonly AppDbContext _db;

    public UserDeviceService(AppDbContext db) => _db = db;

    public async Task<(UserDevice Device, bool IsNewDevice)> TrustOnSignInAsync(
        User user,
        string? deviceId,
        string? deviceName,
        string? userAgent,
        bool rememberTwoFactor)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return (new UserDevice { UserId = user.Id, DeviceId = "" }, false);
        }

        var id = deviceId.Trim();
        var row = await _db.UserDevices.FirstOrDefaultAsync(d => d.UserId == user.Id && d.DeviceId == id);
        var now = DateTime.UtcNow;
        var isNew = row == null || row.TrustedAt == null;

        if (row == null)
        {
            row = new UserDevice
            {
                UserId = user.Id,
                DeviceId = id,
                Name = string.IsNullOrWhiteSpace(deviceName) ? "Device" : deviceName.Trim(),
                UserAgent = userAgent,
                FirstSeenAt = now,
                LastSeenAt = now,
                TrustedAt = now,
                TwoFactorTrustedUntil = rememberTwoFactor ? now.Add(AuthSessionService.TwoFactorRememberDuration) : null
            };
            _db.UserDevices.Add(row);
        }
        else
        {
            row.LastSeenAt = now;
            row.TrustedAt = now;
            if (!string.IsNullOrWhiteSpace(deviceName))
                row.Name = deviceName.Trim();
            if (!string.IsNullOrWhiteSpace(userAgent))
                row.UserAgent = userAgent;
            if (rememberTwoFactor)
                row.TwoFactorTrustedUntil = now.Add(AuthSessionService.TwoFactorRememberDuration);
        }

        await _db.SaveChangesAsync();
        return (row, isNew);
    }

    public async Task<bool> SkipsTwoFactorAsync(Guid userId, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        var id = deviceId.Trim();
        var row = await _db.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == id);
        return row?.TwoFactorTrustedUntil is { } until && until > DateTime.UtcNow;
    }
}
