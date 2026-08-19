using App.Core.Cloud;

namespace App.Core.Storage;

/// <summary>
/// Named stack: chat model plus image / speech / routing / vision-proxy slots.
/// Slot values are catalog ids (<c>lemonade/…</c>, <c>ollama/…</c>, <c>cloud/{provider}/{model}</c>)
/// or, for provider-level TTS/STT, <c>cloud/{provider}</c>.
/// </summary>
public sealed class ModelProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ChatModelId { get; set; }
    public string? RoutingModelId { get; set; }
    public string? ImageModelId { get; set; }
    public string? EditModelId { get; set; }
    public string? TtsModelId { get; set; }
    public string? SttModelId { get; set; }
    public string? Voice { get; set; }
    public string? VisionProxyModelId { get; set; }

    public ModelProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        ChatModelId = ChatModelId,
        RoutingModelId = RoutingModelId,
        ImageModelId = ImageModelId,
        EditModelId = EditModelId,
        TtsModelId = TtsModelId,
        SttModelId = SttModelId,
        Voice = Voice,
        VisionProxyModelId = VisionProxyModelId
    };
}

public static class ModelProfileId
{
    public const string PickerPrefix = "profile/";

    public static string ForPicker(string profileId) => PickerPrefix + profileId.Trim();

    public static bool TryParsePicker(string? value, out string profileId)
    {
        profileId = "";
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(PickerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        profileId = value[PickerPrefix.Length..].Trim();
        return profileId.Length > 0;
    }

    /// <summary>
    /// Stack extras (image, speech, vision proxy, routing override) apply only when
    /// the Chat picker is a profile. A raw model is that model plus its own tools/vision.
    /// </summary>
    public static ModelProfile? GetSelectedProfile(IKeyStore keys, string? pickerOrCatalogId = null)
    {
        var picker = pickerOrCatalogId ?? keys.LastSelectedModel;
        if (!TryParsePicker(picker, out var pid))
            return null;
        return keys.GetModelProfile(pid);
    }

    public static bool IsProfilePicker(string? pickerOrCatalogId) =>
        TryParsePicker(pickerOrCatalogId, out _);

    public static string? ResolveChatModelId(IKeyStore keys, string? pickerOrCatalogId)
    {
        if (TryParsePicker(pickerOrCatalogId, out var pid))
            return keys.GetModelProfile(pid)?.ChatModelId;
        return pickerOrCatalogId;
    }

    public static string? ResolveRoutingModelId(IKeyStore keys, string? pickerOrCatalogId = null)
    {
        var fromProfile = GetSelectedProfile(keys, pickerOrCatalogId)?.RoutingModelId;
        if (!string.IsNullOrWhiteSpace(fromProfile))
            return fromProfile;
        return keys.ToolRoutingModelId;
    }

    public static string? ResolveImageModelId(IKeyStore keys, string? pickerOrCatalogId = null) =>
        NullIfEmpty(GetSelectedProfile(keys, pickerOrCatalogId)?.ImageModelId);

    public static string? ResolveEditModelId(IKeyStore keys, string? pickerOrCatalogId = null)
    {
        var p = GetSelectedProfile(keys, pickerOrCatalogId);
        if (p == null)
            return null;
        return NullIfEmpty(p.EditModelId) ?? NullIfEmpty(p.ImageModelId);
    }

    public static string? ResolveTtsModelId(IKeyStore keys, string? pickerOrCatalogId = null) =>
        NullIfEmpty(GetSelectedProfile(keys, pickerOrCatalogId)?.TtsModelId);

    public static string? ResolveSttModelId(IKeyStore keys, string? pickerOrCatalogId = null) =>
        NullIfEmpty(GetSelectedProfile(keys, pickerOrCatalogId)?.SttModelId);

    public static string? ResolveVoice(IKeyStore keys, string? pickerOrCatalogId = null) =>
        NullIfEmpty(GetSelectedProfile(keys, pickerOrCatalogId)?.Voice);

    public static string? ResolveVisionProxyModelId(IKeyStore keys, string? pickerOrCatalogId = null) =>
        NullIfEmpty(GetSelectedProfile(keys, pickerOrCatalogId)?.VisionProxyModelId);

    public static bool TryCloudProvider(string? catalogId, out string providerId, out string? modelName)
    {
        providerId = "";
        modelName = null;
        if (string.IsNullOrWhiteSpace(catalogId))
            return false;
        if (CloudModelId.TryParse(catalogId, out providerId, out var name))
        {
            modelName = name;
            return true;
        }

        if (catalogId.StartsWith("cloud/", StringComparison.OrdinalIgnoreCase))
        {
            providerId = catalogId["cloud/".Length..].Trim();
            if (providerId.Length == 0) return false;
            modelName = null;
            return true;
        }

        return false;
    }

    public static bool IsLemonadeCatalog(string? catalogId) =>
        !string.IsNullOrWhiteSpace(catalogId) &&
        catalogId.StartsWith("lemonade/", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
