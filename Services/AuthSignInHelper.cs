using System.Security.Claims;
using App.Data;
using Microsoft.AspNetCore.Authentication;

namespace App.Services;

public static class AuthSignInHelper
{
    public static async Task SignInUserAsync(HttpContext ctx, User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString())
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

    public static async Task SignOutUserAsync(HttpContext ctx)
    {
        // Must match the scheme used by SignInUserAsync and Program.cs AddCookie("AppAuth").
        // CookieAuthenticationDefaults.AuthenticationScheme is "Cookies" and does NOT clear
        // the AppAuth cookie — that left WASM sessions alive after "Sign out".
        await ctx.SignOutAsync("AppAuth");
    }
}