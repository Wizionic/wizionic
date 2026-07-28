namespace ChatfishApp.Core.Lemonade;

/// <summary>
/// Client for Lemonade OpenAI-compatible image generation, editing, and upscale endpoints.
/// </summary>
public interface ILemonadeImageService
{
    /// <summary>True when a Lemonade base URL is configured and at least one image-capable model is known.</summary>
    bool IsGenerateAvailable { get; }

    /// <summary>True when at least one model has the Lemonade <c>edit</c> label.</summary>
    bool IsEditAvailable { get; }

    string? DefaultImageModel { get; }
    string? DefaultEditModel { get; }

    IReadOnlyList<string> ImageModelNames { get; }
    IReadOnlyList<string> EditModelNames { get; }

    /// <summary>Whether the named model is labeled for image editing (<c>edit</c>).</summary>
    bool ModelSupportsEdit(string? modelName);

    /// <summary>Whether the named model can generate images (<c>image</c> or <c>edit</c>).</summary>
    bool ModelSupportsGenerate(string? modelName);

    Task<LemonadeImageResult> GenerateAsync(LemonadeImageGenerateRequest request, CancellationToken ct = default);

    Task<LemonadeImageResult> EditAsync(LemonadeImageEditRequest request, CancellationToken ct = default);

    /// <summary>4× upscale via Real-ESRGAN (<c>POST /v1/images/upscale</c>).</summary>
    Task<LemonadeImageResult> UpscaleAsync(string base64Png, string upscaleModel, CancellationToken ct = default);
}

public sealed record LemonadeImageGenerateRequest(
    string Prompt,
    string? Model = null,
    string Size = "512x512",
    int? Steps = null,
    double? CfgScale = null,
    long? Seed = null);

public sealed record LemonadeImageEditRequest(
    string Prompt,
    byte[] ImagePngBytes,
    string? Model = null,
    string Size = "512x512",
    int? Steps = null,
    double? CfgScale = null,
    long? Seed = null,
    byte[]? MaskPngBytes = null);

public sealed record LemonadeImageResult(
    bool Success,
    string? Base64Png = null,
    string? Model = null,
    string? Error = null,
    long? RevisedSeed = null)
{
    public static LemonadeImageResult Fail(string error) => new(false, Error: error);
}
