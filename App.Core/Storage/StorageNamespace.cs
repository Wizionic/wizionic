using System.Security.Cryptography;
using System.Text;
using App.Core.Auth;

namespace App.Core.Storage;

public static class StorageNamespace
{
    /// <summary>Guest / signed-out local data prefix (matches historical chat/note isolation).</summary>
    public const string GuestPrefix = "wasmchat-";

    public static string GetPrefix(IAuthService auth)
    {
        if (auth.IsAuthenticated)
        {
            if (!string.IsNullOrEmpty(auth.UserId))
                return $"u-{auth.UserId}-";
            if (!string.IsNullOrEmpty(auth.Email))
                return $"e-{GetStableHash(auth.Email)}-";
        }

        return GuestPrefix;
    }

    /// <summary>Per-user/guest storage key: <c>{prefix}{baseKey}</c>.</summary>
    public static string PrefixedKey(IAuthService auth, string baseKey) =>
        GetPrefix(auth) + baseKey;

    private static string GetStableHash(string input)
    {
        if (string.IsNullOrEmpty(input)) return "00000000";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}