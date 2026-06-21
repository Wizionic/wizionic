using System.Security.Cryptography;
using System.Text;
using ChatfishApp.Core.Auth;

namespace ChatfishApp.Core.Storage;

public static class StorageNamespace
{
    public static string GetPrefix(IAuthService auth)
    {
        if (auth.IsAuthenticated)
        {
            if (!string.IsNullOrEmpty(auth.UserId))
                return $"u-{auth.UserId}-";
            if (!string.IsNullOrEmpty(auth.Email))
                return $"e-{GetStableHash(auth.Email)}-";
        }

        return "wasmchat-";
    }

    private static string GetStableHash(string input)
    {
        if (string.IsNullOrEmpty(input)) return "00000000";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}