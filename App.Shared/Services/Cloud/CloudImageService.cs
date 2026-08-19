using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using App.Core.Cloud;
using App.Core.Storage;

namespace App.Shared.Services.Cloud;

public sealed class CloudImageService : ICloudImageService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly IKeyStore _keyStore;

    public CloudImageService(IKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    public bool IsGenerateAvailable(string providerId) => ImageModelNames(providerId).Count > 0;

    public bool IsEditAvailable(string providerId) => EditModelNames(providerId).Count > 0;

    public string? DefaultImageModel(string providerId)
    {
        var p = _keyStore.GetCloudProvider(providerId);
        if (p == null) return null;
        var names = ImageModelNames(providerId);
        if (!string.IsNullOrWhiteSpace(p.DefaultImageModel) &&
            names.Any(n => n.Equals(p.DefaultImageModel, StringComparison.OrdinalIgnoreCase)))
            return p.DefaultImageModel;
        return names.FirstOrDefault();
    }

    public string? DefaultEditModel(string providerId)
    {
        var p = _keyStore.GetCloudProvider(providerId);
        if (p == null) return null;
        var names = EditModelNames(providerId);
        if (!string.IsNullOrWhiteSpace(p.DefaultEditModel) &&
            names.Any(n => n.Equals(p.DefaultEditModel, StringComparison.OrdinalIgnoreCase)))
            return p.DefaultEditModel;
        return names.FirstOrDefault();
    }

    public IReadOnlyList<string> ImageModelNames(string providerId) =>
        _keyStore.GetCloudProvider(providerId)?.Models
            .Where(m => m.IsImage || m.IsEdit)
            .Select(m => m.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();

    public IReadOnlyList<string> EditModelNames(string providerId) =>
        _keyStore.GetCloudProvider(providerId)?.Models
            .Where(m => m.IsEdit)
            .Select(m => m.Name)
            .ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();

    public bool ModelSupportsEdit(string providerId, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        var bare = StripPrefix(modelName);
        return _keyStore.GetCloudProvider(providerId)?.Models
            .Any(m => m.IsEdit && m.Name.Equals(bare, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public bool ModelSupportsGenerate(string providerId, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        var bare = StripPrefix(modelName);
        var m = _keyStore.GetCloudProvider(providerId)?.Models
            .FirstOrDefault(x => x.Name.Equals(bare, StringComparison.OrdinalIgnoreCase));
        return m is { IsImage: true } or { IsEdit: true };
    }

    public async Task<CloudImageResult> GenerateAsync(CloudImageGenerateRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return CloudImageResult.Fail("Prompt is required.");

        var provider = _keyStore.GetCloudProvider(request.ProviderId);
        if (provider == null)
            return CloudImageResult.Fail("Cloud provider not found.");

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? DefaultImageModel(request.ProviderId)
            : StripPrefix(request.Model);
        if (string.IsNullOrWhiteSpace(model))
            return CloudImageResult.Fail("No image model configured. Refresh models on Cloud providers.");

        var origin = CloudModelCatalogResolver.NormalizeBaseUrl(provider.BaseUrl);
        var url = origin + "/images/generations";
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = request.Prompt.Trim(),
            ["n"] = 1,
            ["response_format"] = "b64_json"
        };

        try
        {
            using var req = CloudModelCatalogResolver.CreateRequest(HttpMethod.Post, url, provider.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await SharedHttp.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return CloudImageResult.Fail(CloudModelCatalogResolver.FormatHttpError(resp.StatusCode, respText));

            return await ParseImageResponseAsync(respText, model, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CloudImageResult.Fail("Image generation was cancelled.");
        }
        catch (Exception ex)
        {
            return CloudImageResult.Fail("Image generation failed: " + ex.Message);
        }
    }

    public async Task<CloudImageResult> EditAsync(CloudImageEditRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return CloudImageResult.Fail("Edit prompt is required.");
        if (request.ImageBytes is not { Length: > 0 })
            return CloudImageResult.Fail("Source image bytes are required.");

        var provider = _keyStore.GetCloudProvider(request.ProviderId);
        if (provider == null)
            return CloudImageResult.Fail("Cloud provider not found.");

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? DefaultEditModel(request.ProviderId)
            : StripPrefix(request.Model);
        if (string.IsNullOrWhiteSpace(model))
            return CloudImageResult.Fail("No image-edit model configured. Refresh models on Cloud providers.");

        var origin = CloudModelCatalogResolver.NormalizeBaseUrl(provider.BaseUrl);
        var url = origin + "/images/edits";
        var mime = string.IsNullOrWhiteSpace(request.ContentType) ? "image/png" : request.ContentType;
        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(request.ImageBytes)}";

        try
        {
            if (provider.HasXaiImageApi)
            {
                var result = await PostJsonEditAsync(url, provider.ApiKey, model, request.Prompt.Trim(), dataUrl, ct);
                if (result.Success)
                    return result;
            }

            var openAi = await PostMultipartEditAsync(url, provider.ApiKey, model, request.Prompt.Trim(), request.ImageBytes, mime, ct);
            if (openAi.Success)
                return openAi;

            if (!provider.HasXaiImageApi)
            {
                var fallback = await PostJsonEditAsync(url, provider.ApiKey, model, request.Prompt.Trim(), dataUrl, ct);
                if (fallback.Success)
                    return fallback;
            }

            return openAi;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CloudImageResult.Fail("Image edit was cancelled.");
        }
        catch (Exception ex)
        {
            return CloudImageResult.Fail("Image edit failed: " + ex.Message);
        }
    }

    private static async Task<CloudImageResult> PostJsonEditAsync(
        string url, string apiKey, string model, string prompt, string dataUrl, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["n"] = 1,
            ["response_format"] = "b64_json",
            ["image"] = new Dictionary<string, object?>
            {
                ["url"] = dataUrl,
                ["type"] = "image_url"
            }
        };

        using var req = CloudModelCatalogResolver.CreateRequest(HttpMethod.Post, url, apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await SharedHttp.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return CloudImageResult.Fail(CloudModelCatalogResolver.FormatHttpError(resp.StatusCode, respText));
        return await ParseImageResponseAsync(respText, model, ct);
    }

    private static async Task<CloudImageResult> PostMultipartEditAsync(
        string url, string apiKey, string model, string prompt, byte[] imageBytes, string mime, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(prompt), "prompt");
        content.Add(new StringContent("b64_json"), "response_format");
        var file = new ByteArrayContent(imageBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        content.Add(file, "image", mime.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? "image.jpg" : "image.png");

        using var req = CloudModelCatalogResolver.CreateRequest(HttpMethod.Post, url, apiKey);
        req.Content = content;
        using var resp = await SharedHttp.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return CloudImageResult.Fail(CloudModelCatalogResolver.FormatHttpError(resp.StatusCode, respText));
        return await ParseImageResponseAsync(respText, model, ct);
    }

    private static async Task<CloudImageResult> ParseImageResponseAsync(string json, string model, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                return CloudImageResult.Fail("Provider returned no image data.");
            }

            var first = data[0];
            if (first.TryGetProperty("b64_json", out var b64El))
            {
                var b64 = b64El.GetString();
                if (string.IsNullOrWhiteSpace(b64))
                    return CloudImageResult.Fail("Provider response missing b64_json image data.");
                return new CloudImageResult(true, Base64Png: b64, Model: model);
            }

            if (first.TryGetProperty("url", out var urlEl))
            {
                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    return CloudImageResult.Fail("Provider returned an empty image URL.");
                using var imgResp = await SharedHttp.GetAsync(url, ct);
                imgResp.EnsureSuccessStatusCode();
                var bytes = await imgResp.Content.ReadAsByteArrayAsync(ct);
                return new CloudImageResult(true, Base64Png: Convert.ToBase64String(bytes), Model: model);
            }

            return CloudImageResult.Fail("Provider response missing b64_json image data.");
        }
        catch (Exception ex)
        {
            return CloudImageResult.Fail("Could not parse image response: " + ex.Message);
        }
    }

    private static string StripPrefix(string modelName)
    {
        if (CloudModelId.TryParse(modelName, out _, out var name))
            return name;
        return modelName.Trim();
    }
}
