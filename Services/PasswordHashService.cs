using System.Security.Cryptography;
using System.Text;
using App.Core.Auth;

namespace App.Services;

/// <summary>
/// PBKDF2-SHA256 password hashing with a per-password random salt.
/// Stored format: pbkdf2-sha256$&lt;iterations&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;
/// </summary>
public static class PasswordHashService
{
    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public const int MinLength = PasswordRules.MinLength;

    public static bool MeetsRequirements(string? password) =>
        PasswordRules.MeetsRequirements(password);

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Pbkdf2(password, salt, Iterations, HashSize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Constant-time-ish verify. Returns false for null/empty stored hash or bad format
    /// (callers should treat "no password set" the same as "wrong password").
    /// </summary>
    public static bool Verify(string? password, string? storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
            return false;

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        if (!int.TryParse(parts[1], out var iterations) || iterations < 10_000)
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch
        {
            return false;
        }

        var actual = Pbkdf2(password, salt, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Burns a similar amount of CPU when no password is set, to avoid timing clues.
    /// </summary>
    public static void DummyVerify(string? password)
    {
        var dummy = Hash(password ?? "x");
        _ = Verify(password ?? "x", dummy);
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations, int length)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            length);
    }
}
