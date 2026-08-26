using System.Security.Cryptography;
using System.Text;

namespace App.Services;

/// <summary>
/// Hashes one-time login codes at rest. New values are prefixed so in-flight plaintext
/// codes from before this change still verify until they expire.
/// </summary>
public static class LoginCodeHasher
{
    public const string Prefix = "sha256:";

    public static string Hash(string normalizedCode)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode));
        return Prefix + Convert.ToHexString(hash);
    }

    public static bool Matches(string? stored, string normalizedCode)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(normalizedCode))
            return false;

        if (stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var expected = Convert.FromHexString(stored[Prefix.Length..]);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        // Legacy plaintext (codes issued before hashing). Ordinal compare.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored),
            Encoding.UTF8.GetBytes(normalizedCode));
    }
}
