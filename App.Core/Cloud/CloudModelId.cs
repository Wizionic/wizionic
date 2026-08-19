namespace App.Core.Cloud;

/// <summary>Catalog ids for user-keyed cloud models: <c>cloud/{providerId}/{modelName}</c>.</summary>
public static class CloudModelId
{
    public const string Prefix = "cloud/";

    public static string Format(string providerId, string modelName) =>
        Prefix + providerId.Trim() + "/" + modelName.Trim();

    public static bool IsCloud(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) &&
        modelId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? modelId, out string providerId, out string modelName)
    {
        providerId = "";
        modelName = "";
        if (!IsCloud(modelId))
            return false;

        var rest = modelId!.Substring(Prefix.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash >= rest.Length - 1)
            return false;

        providerId = rest[..slash];
        modelName = rest[(slash + 1)..];
        return providerId.Length > 0 && modelName.Length > 0;
    }
}
