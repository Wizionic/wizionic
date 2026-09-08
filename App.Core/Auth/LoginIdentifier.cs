namespace App.Core.Auth;

/// <summary>
/// Login id stored in <c>User.Email</c>: a real email, or a local Home Server username.
/// </summary>
public static class LoginIdentifier
{
    public const int LocalMinLength = 3;
    public const int LocalMaxLength = 32;

    public static string Normalize(string? value) => EmailNormalizer.Normalize(value);

    public static bool LooksLikeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var at = trimmed.IndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1)
            return false;

        var dot = trimmed.LastIndexOf('.');
        if (dot <= at + 1 || dot == trimmed.Length - 1)
            return false;

        return !trimmed.Contains(' ') && trimmed.Length is >= 5 and <= 254;
    }

    public static bool IsValidLoginId(string? value) =>
        TryValidate(value, out _, out _);

    public static bool TryValidate(string? value, out string normalized, out string? error)
    {
        normalized = Normalize(value);
        error = null;

        if (string.IsNullOrEmpty(normalized))
        {
            error = "Enter a username or email.";
            return false;
        }

        if (normalized.Contains('@'))
        {
            if (!LooksLikeEmail(normalized))
            {
                error = "Enter a valid email, or a username without @.";
                return false;
            }

            return true;
        }

        if (normalized.Length < LocalMinLength || normalized.Length > LocalMaxLength)
        {
            error = "Username must be 3–32 characters.";
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(normalized[0]))
        {
            error = "Username must start with a letter or number.";
            return false;
        }

        foreach (var c in normalized)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                continue;

            error = "Username can use letters, numbers, period, underscore, and hyphen.";
            return false;
        }

        return true;
    }
}
