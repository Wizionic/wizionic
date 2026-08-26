using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using App.Core.Auth;
using App.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

public sealed class AuthSessionService
{
    public static readonly TimeSpan TwoFactorRememberDuration = TimeSpan.FromDays(30);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;

    public AuthSessionService(AppDbContext db) => _db = db;

    public async Task SignInAsync(
        HttpContext ctx,
        User user,
        string? deviceId,
        string? deviceName,
        bool rememberTwoFactor = false)
    {
        var rawSid = NewSecret();
        var session = new AuthSession
        {
            UserId = user.Id,
            SessionHash = HashSecret(rawSid),
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim(),
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim(),
            UserAgent = Truncate(ctx.Request.Headers.UserAgent.ToString(), 240),
            Ip = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            TwoFactorTrustedUntil = rememberTwoFactor ? DateTime.UtcNow.Add(TwoFactorRememberDuration) : null
        };
        _db.AuthSessions.Add(session);
        await _db.SaveChangesAsync();

        await SignInCookieAsync(ctx, user, rawSid);
    }

    /// <summary>
    /// Existing cookies from before server-side sessions get a row instead of being rejected,
    /// so nobody is signed out of their notes. Encryption keys are never rotated here.
    /// </summary>
    public async Task UpgradeLegacyCookieAsync(CookieValidatePrincipalContext ctx)
    {
        var email = ctx.Principal?.Identity?.Name;
        var userIdStr = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(email) || !Guid.TryParse(userIdStr, out var userId))
            return;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return;

        var deviceId = ReadDeviceId(ctx.HttpContext);
        var deviceName = ReadDeviceName(ctx.HttpContext);
        var rawSid = NewSecret();
        _db.AuthSessions.Add(new AuthSession
        {
            UserId = user.Id,
            SessionHash = HashSecret(rawSid),
            DeviceId = deviceId,
            DeviceName = deviceName,
            UserAgent = Truncate(ctx.Request.Headers.UserAgent.ToString(), 240),
            Ip = ctx.HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var identity = new ClaimsIdentity(ctx.Principal!.Identity);
        ReplaceSidClaim(identity, rawSid);
        ctx.ReplacePrincipal(new ClaimsPrincipal(identity));
        ctx.ShouldRenew = true;
    }

    public async Task<AuthSession?> FindValidAsync(string? rawSid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawSid))
            return null;

        var hash = HashSecret(rawSid.Trim());
        var session = await _db.AuthSessions.FirstOrDefaultAsync(s => s.SessionHash == hash, ct);
        if (session == null || session.RevokedAt != null)
            return null;

        return session;
    }

    public async Task TouchAsync(AuthSession session, CancellationToken ct = default)
    {
        if (DateTime.UtcNow - session.LastSeenAt < TouchInterval)
            return;
        session.LastSeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAsync(Guid sessionRowId, Guid userId, CancellationToken ct = default)
    {
        var session = await _db.AuthSessions.FirstOrDefaultAsync(s => s.Id == sessionRowId && s.UserId == userId, ct);
        if (session == null || session.RevokedAt != null)
            return;
        session.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeOthersAsync(Guid userId, string? currentRawSid, CancellationToken ct = default)
    {
        var currentHash = string.IsNullOrWhiteSpace(currentRawSid) ? null : HashSecret(currentRawSid.Trim());
        var others = await _db.AuthSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && (currentHash == null || s.SessionHash != currentHash))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var s in others)
            s.RevokedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeCurrentAsync(string? rawSid, CancellationToken ct = default)
    {
        var session = await FindValidAsync(rawSid, ct);
        if (session == null)
            return;
        session.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuthSessionInfo>> ListAsync(Guid userId, string? currentRawSid, CancellationToken ct = default)
    {
        var currentHash = string.IsNullOrWhiteSpace(currentRawSid) ? null : HashSecret(currentRawSid.Trim());
        var rows = await _db.AuthSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastSeenAt)
            .Take(50)
            .ToListAsync(ct);

        return rows.Select(s => new AuthSessionInfo
        {
            Id = s.Id.ToString("N"),
            DeviceName = string.IsNullOrWhiteSpace(s.DeviceName) ? "Device" : s.DeviceName,
            DeviceId = s.DeviceId,
            CreatedAt = s.CreatedAt,
            LastSeenAt = s.LastSeenAt,
            IsCurrent = currentHash != null && string.Equals(s.SessionHash, currentHash, StringComparison.Ordinal),
            UserAgent = s.UserAgent
        }).ToList();
    }

    /// <summary>
    /// Whether this request may fetch the account encryption key / join sync.
    /// Legacy unbound sessions and old clients that omit the device header are allowed
    /// so existing notes stay reachable. A bound session used from a *different*
    /// device id is denied until that device completes a fresh sign-in.
    /// </summary>
    public static bool DeviceMayUseSession(AuthSession session, string? requestDeviceId)
    {
        if (string.IsNullOrWhiteSpace(session.DeviceId))
            return true;
        if (string.IsNullOrWhiteSpace(requestDeviceId))
            return true;
        return string.Equals(session.DeviceId, requestDeviceId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string? ReadSid(ClaimsPrincipal? user) =>
        user?.FindFirstValue(ClientDeviceKeys.SessionClaimType);

    public static string? ReadDeviceId(HttpContext ctx)
    {
        var value = ctx.Request.Headers[ClientDeviceKeys.IdHeader].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string? ReadDeviceName(HttpContext ctx)
    {
        var value = ctx.Request.Headers[ClientDeviceKeys.NameHeader].ToString();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            value = Uri.UnescapeDataString(value.Trim());
        }
        catch
        {
            value = value.Trim();
        }
        return Truncate(value, 80);
    }

    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes);
    }

    public static string HashSecret(string raw)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private static async Task SignInCookieAsync(HttpContext ctx, User user, string rawSid)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClientDeviceKeys.SessionClaimType, rawSid)
        };
        var identity = new ClaimsIdentity(claims, "AppAuth");
        var principal = new ClaimsPrincipal(identity);
        var authProps = new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true
        };
        await ctx.SignInAsync("AppAuth", principal, authProps);
    }

    private static void ReplaceSidClaim(ClaimsIdentity identity, string rawSid)
    {
        var existing = identity.FindAll(ClientDeviceKeys.SessionClaimType).ToList();
        foreach (var c in existing)
            identity.RemoveClaim(c);
        identity.AddClaim(new Claim(ClientDeviceKeys.SessionClaimType, rawSid));
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Length <= max ? value : value[..max];
    }
}
