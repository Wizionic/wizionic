using System.Security.Claims;
using ChatfishApp.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ChatfishApp.Services;

public static class AuthSignInHelper
{
    public static async Task SignInUserAsync(HttpContext ctx, User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString())
        };

        var identity = new ClaimsIdentity(claims, "ChatfishAuth");
        var principal = new ClaimsPrincipal(identity);

        var authProps = new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true
        };

        await ctx.SignInAsync("ChatfishAuth", principal, authProps);
    }

    public static async Task SignOutUserAsync(HttpContext ctx)
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}