using System.Security.Cryptography;
using System.Text;
using App.Core.Auth;
using Konscious.Security.Cryptography;

namespace App.Services;

/// <summary>
/// Password hashing. New hashes are Argon2id; existing PBKDF2-SHA256 hashes still verify
/// and are rehashed to Argon2id on the next successful password login.
/// Stored Argon2 format: argon2id$v=19$m=&lt;kib&gt;,t=&lt;iter&gt;,p=&lt;p&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;
/// Stored PBKDF2 format: pbkdf2-sha256$&lt;iterations&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;
/// </summary>
public static class PasswordHashService
{
    private const string ArgonPrefix = "argon2id";
    private const string PbkdfPrefix = "pbkdf2-sha256";
    private const int ArgonMemoryKib = 19_456;
    private const int ArgonIterations = 2;
    private const int ArgonParallelism = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public const int MinLength = PasswordRules.MinLength;

    public static bool MeetsRequirements(string? password) =>
        PasswordRules.MeetsRequirements(password);

    public static bool NeedsRehash(string? storedHash) =>
        !string.IsNullOrEmpty(storedHash) &&
        !storedHash.StartsWith(ArgonPrefix + "$", StringComparison.Ordinal);

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Argon2id(password, salt, ArgonMemoryKib, ArgonIterations, ArgonParallelism, HashSize);
        return $"{ArgonPrefix}$v=19$m={ArgonMemoryKib},t={ArgonIterations},p={ArgonParallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? password, string? storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
            return false;

        if (storedHash.StartsWith(ArgonPrefix + "$", StringComparison.Ordinal))
            return VerifyArgon2(password, storedHash);

        if (storedHash.StartsWith(PbkdfPrefix + "$", StringComparison.Ordinal))
            return VerifyPbkdf2(password, storedHash);

        return false;
    }

    /// <summary>
    /// Burns a similar amount of CPU when no password is set, to avoid timing clues.
    /// </summary>
    public static void DummyVerify(string? password)
    {
        var dummy = Hash(password ?? "x");
        _ = Verify(password ?? "x", dummy);
    }

    private static bool VerifyArgon2(string password, string storedHash)
    {
        // argon2id$v=19$m=19456,t=2,p=1$salt$hash
        var parts = storedHash.Split('$');
        if (parts.Length != 5)
            return false;

        var paramParts = parts[2].Split(',');
        int m = ArgonMemoryKib, t = ArgonIterations, p = ArgonParallelism;
        foreach (var piece in paramParts)
        {
            if (piece.StartsWith("m=", StringComparison.Ordinal) && int.TryParse(piece[2..], out var mv))
                m = mv;
            else if (piece.StartsWith("t=", StringComparison.Ordinal) && int.TryParse(piece[2..], out var tv))
                t = tv;
            else if (piece.StartsWith("p=", StringComparison.Ordinal) && int.TryParse(piece[2..], out var pv))
                p = pv;
        }

        if (m is < 8_192 or > 1_048_576 || t is < 1 or > 16 || p is < 1 or > 8)
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch
        {
            return false;
        }

        var actual = Argon2id(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool VerifyPbkdf2(string password, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 4)
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

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Argon2id(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            MemorySize = memoryKib,
            Iterations = iterations
        };
        return argon.GetBytes(length);
    }
}
