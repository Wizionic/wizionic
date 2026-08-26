namespace App.Core.Auth;

/// <summary>
/// Shared password-shape checks for the account password form (server and UI).
/// NIST 800-63B: length + known-bad list; no composition rules and no rotation.
/// </summary>
public static class PasswordRules
{
    public const int MinLength = 8;

    public const string RequirementsText =
        "Use at least 8 characters. Avoid common or leaked passwords.";

    public static bool MeetsRequirements(string? password) =>
        TryValidate(password, out _);

    public static bool TryValidate(string? password, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            error = RequirementsText;
            return false;
        }

        if (password.Length > 256)
        {
            error = "Password is too long.";
            return false;
        }

        if (IsCommon(password))
        {
            error = "Choose something less common.";
            return false;
        }

        return true;
    }

    private static bool IsCommon(string password)
    {
        var normalized = password.Trim().ToLowerInvariant();
        return Common.Contains(normalized);
    }

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "password", "password1", "password12", "password123", "password1234",
        "password!", "password1!", "passw0rd", "p@ssword", "p@ssw0rd",
        "123456", "1234567", "12345678", "123456789", "1234567890",
        "12345678", "qwerty", "qwerty1", "qwerty123", "letmein", "welcome",
        "welcome1", "welcome123", "admin", "admin1", "admin123",
        "changeme", "iloveyou", "abc123", "monkey", "dragon",
        "master", "login", "login123", "wizionic", "wizionic1",
        "wizionic123", "trustno1", "secret", "secret123",
        "password8", "password88", "password888",
    };
}
