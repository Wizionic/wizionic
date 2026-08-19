namespace App.Core.Cloud;

public interface ICloudImageService
{
    bool IsGenerateAvailable(string providerId);
    bool IsEditAvailable(string providerId);
    string? DefaultImageModel(string providerId);
    string? DefaultEditModel(string providerId);
    IReadOnlyList<string> ImageModelNames(string providerId);
    IReadOnlyList<string> EditModelNames(string providerId);
    bool ModelSupportsEdit(string providerId, string? modelName);
    bool ModelSupportsGenerate(string providerId, string? modelName);

    Task<CloudImageResult> GenerateAsync(CloudImageGenerateRequest request, CancellationToken ct = default);
    Task<CloudImageResult> EditAsync(CloudImageEditRequest request, CancellationToken ct = default);
}

public sealed record CloudImageGenerateRequest(
    string ProviderId,
    string Prompt,
    string? Model = null);

public sealed record CloudImageEditRequest(
    string ProviderId,
    string Prompt,
    byte[] ImageBytes,
    string? Model = null,
    string ContentType = "image/png");

public sealed record CloudImageResult(
    bool Success,
    string? Base64Png = null,
    string? Model = null,
    string? Error = null)
{
    public static CloudImageResult Fail(string error) => new(false, Error: error);
}
