namespace App.Core.SmartHome;

public static class HomeAssistantCredentials
{
    public static string NormalizeToken(string token)
    {
        token = (token ?? "").Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();
        return token;
    }

    public static bool TryNormalize(string baseUrl, string token, out string normalizedUrl, out string normalizedToken)
    {
        normalizedUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        normalizedToken = NormalizeToken(token);
        return !string.IsNullOrWhiteSpace(normalizedUrl) && !string.IsNullOrWhiteSpace(normalizedToken);
    }
}