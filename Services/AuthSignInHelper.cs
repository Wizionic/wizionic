using App.Data;
using Microsoft.AspNetCore.Authentication;

namespace App.Services;

public static class AuthSignInHelper
{
    public static async Task CompleteSignInAsync(
        HttpContext ctx,
        User user,
        AuthSessionService sessions,
        UserDeviceService devices,
        IEmailSender email,
        bool rememberTwoFactor = false)
    {
        var deviceId = AuthSessionService.ReadDeviceId(ctx);
        var deviceName = AuthSessionService.ReadDeviceName(ctx);
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var (_, isNew) = await devices.TrustOnSignInAsync(user, deviceId, deviceName, ua, rememberTwoFactor);
        await sessions.SignInAsync(ctx, user, deviceId, deviceName, rememberTwoFactor);

        if (isNew && !string.IsNullOrWhiteSpace(user.Email))
        {
            var (subj, text, html) = SecurityEmailContent.NewDevice(
                deviceName,
                ctx.Connection.RemoteIpAddress?.ToString(),
                DateTime.UtcNow);
            try
            {
                await email.SendSecurityNoticeAsync(user.Email, subj, text, html);
            }
            catch
            {
                // Never fail sign-in because mail failed.
            }
        }
    }

    public static async Task SignOutUserAsync(HttpContext ctx, AuthSessionService sessions)
    {
        var sid = AuthSessionService.ReadSid(ctx.User);
        try
        {
            await sessions.RevokeCurrentAsync(sid);
        }
        catch
        {
            // Still clear the cookie.
        }

        await ctx.SignOutAsync("AppAuth");
    }
}
