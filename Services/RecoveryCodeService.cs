using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using App.Data;

namespace App.Services;

public static class RecoveryCodeService
{
    public const int CodeCount = 8;
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string[] Generate()
    {
        var codes = new string[CodeCount];
        for (var i = 0; i < CodeCount; i++)
            codes[i] = NewCode();
        return codes;
    }

    public static void StoreHashes(User user, IEnumerable<string> codes)
    {
        var hashes = codes.Select(Hash).ToList();
        user.RecoveryCodesJson = JsonSerializer.Serialize(hashes);
    }

    public static bool TryConsume(User user, string? code)
    {
        var normalized = Normalize(code);
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrEmpty(user.RecoveryCodesJson))
            return false;

        List<string>? hashes;
        try
        {
            hashes = JsonSerializer.Deserialize<List<string>>(user.RecoveryCodesJson);
        }
        catch
        {
            return false;
        }

        if (hashes == null || hashes.Count == 0)
            return false;

        var want = Hash(normalized);
        var idx = hashes.FindIndex(h => string.Equals(h, want, StringComparison.Ordinal));
        if (idx < 0)
            return false;

        hashes.RemoveAt(idx);
        user.RecoveryCodesJson = hashes.Count == 0 ? null : JsonSerializer.Serialize(hashes);
        return true;
    }

    public static int RemainingCount(User user)
    {
        if (string.IsNullOrEmpty(user.RecoveryCodesJson))
            return 0;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(user.RecoveryCodesJson)?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    public static void Clear(User user) => user.RecoveryCodesJson = null;

    private static string NewCode()
    {
        Span<char> buffer = stackalloc char[8];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(buffer[..4]) + "-" + new string(buffer[4..]);
    }

    private static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "";
        var chars = code.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray();
        return new string(chars).ToUpperInvariant();
    }

    private static string Hash(string normalized)
    {
        var compact = Normalize(normalized);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(compact));
        return Convert.ToHexString(hash);
    }
}
