namespace App.Core.Auth;

/// <summary>
/// Result of a password login or 2FA verify call.
/// When <see cref="RequiresTwoFactor"/> is true, no session cookie has been issued yet.
/// </summary>
public sealed class AuthLoginResult
{
    public bool Success { get; init; }
    public bool RequiresTwoFactor { get; init; }
    public string? ChallengeId { get; init; }
    public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>();
    public string? MaskedPhone { get; init; }
    public string? Error { get; init; }

    public static AuthLoginResult Ok() => new() { Success = true };

    public static AuthLoginResult Fail(string error) => new() { Error = error };

    public static AuthLoginResult NeedSecondFactor(
        string challengeId,
        IReadOnlyList<string> methods,
        string? maskedPhone) =>
        new()
        {
            Success = true,
            RequiresTwoFactor = true,
            ChallengeId = challengeId,
            Methods = methods,
            MaskedPhone = maskedPhone
        };
}
