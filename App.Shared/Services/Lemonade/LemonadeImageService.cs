using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using App.Core.Lemonade;
using App.Core.Storage;

namespace App.Shared.Services.Lemonade;

/// <summary>
/// Calls Lemonade <c>POST /v1/images/generations</c>, <c>/edits</c>, and <c>/upscale</c>.
/// Shared by WASM and MAUI (plain HttpClient).
/// </summary>
public sealed class LemonadeImageService : ILemonadeImageService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly IKeyStore _keyStore;

    public LemonadeImageService(IKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    public bool IsGenerateAvailable => ImageModelNames.Count > 0;

    public bool IsEditAvailable => EditModelNames.Count > 0;

    public string? DefaultImageModel
    {
        get
        {
            var d = _keyStore.LemonadeDefaultImageModel;
            if (!string.IsNullOrWhiteSpace(d) && ImageModelNames.Any(n => n.Equals(d, StringComparison.OrdinalIgnoreCase)))
                return d;
            return ImageModelNames.FirstOrDefault();
        }
    }

    public string? DefaultEditModel
    {
        get
        {
            var d = _keyStore.LemonadeDefaultEditModel;
            if (!string.IsNullOrWhiteSpace(d) && EditModelNames.Any(n => n.Equals(d, StringComparison.OrdinalIgnoreCase)))
                return d;
            return EditModelNames.FirstOrDefault();
        }
    }

    public IReadOnlyList<string> ImageModelNames =>
        _keyStore.LemonadeModelSettingsList
            .Where(m => m.IsImage || m.IsEdit)
            .Select(m => m.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Only models with Lemonade <c>edit</c> label (e.g. Flux-2-Klein).</summary>
    public IReadOnlyList<string> EditModelNames =>
        _keyStore.LemonadeModelSettingsList
            .Where(m => m.IsEdit)
            .Select(m => m.Name)
            .ToList();

    public bool ModelSupportsEdit(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        var bare = StripPrefix(modelName);
        return _keyStore.GetLemonadeModelSettings(bare)?.IsEdit == true;
    }

    public bool ModelSupportsGenerate(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        var bare = StripPrefix(modelName);
        var s = _keyStore.GetLemonadeModelSettings(bare);
        return s is { IsImage: true } or { IsEdit: true };
    }

    public async Task<LemonadeImageResult> GenerateAsync(LemonadeImageGenerateRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return LemonadeImageResult.Fail("Prompt is required.");

        var model = string.IsNullOrWhiteSpace(request.Model) ? DefaultImageModel : request.Model.Trim();
        model = StripPrefix(model ?? "");
        if (string.IsNullOrWhiteSpace(model))
            return LemonadeImageResult.Fail("No Lemonade image model configured. Refresh models on Local AI and set a default image model.");

        var origin = LemonadeModelCatalogResolver.NormalizeBaseUrl(_keyStore.LemonadeBaseUrl);
        var url = origin + "/v1/images/generations";

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = request.Prompt.Trim(),
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? "512x512" : request.Size.Trim(),
            ["n"] = 1,
            ["response_format"] = "b64_json"
        };
        if (request.Steps is > 0)
            body["steps"] = request.Steps.Value;
        if (request.CfgScale is not null)
            body["cfg_scale"] = request.CfgScale.Value;
        if (request.Seed is not null)
            body["seed"] = request.Seed.Value;

        try
        {
            var json = JsonSerializer.Serialize(body);
            using var req = LemonadeModelCatalogResolver.CreateRequest(HttpMethod.Post, url, _keyStore.LemonadeApiKey);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await SharedHttp.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return LemonadeImageResult.Fail(FormatHttpError(resp.StatusCode, respText));

            return ParseImageResponse(respText, model);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LemonadeImageResult.Fail("Image generation was cancelled.");
        }
        catch (Exception ex)
        {
            return LemonadeImageResult.Fail(FriendlyNetworkError(ex, "generate"));
        }
    }

    public async Task<LemonadeImageResult> EditAsync(LemonadeImageEditRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return LemonadeImageResult.Fail("Edit prompt is required.");
        if (request.ImagePngBytes is not { Length: > 0 })
            return LemonadeImageResult.Fail("Source image bytes are required.");

        var model = string.IsNullOrWhiteSpace(request.Model) ? DefaultEditModel : request.Model.Trim();
        model = StripPrefix(model ?? "");
        if (string.IsNullOrWhiteSpace(model))
            return LemonadeImageResult.Fail(
                "No edit-capable Lemonade model configured. Install a model labeled “edit” (e.g. Flux-2-Klein) and refresh Local AI.");

        if (!ModelSupportsEdit(model))
        {
            return LemonadeImageResult.Fail(
                $"{model} does not support image editing (Lemonade only lists the “image” label, not “edit”). " +
                "Use an edit-capable model such as Flux-2-Klein, or generate a new image instead. " +
                "Z-Image-Turbo is generate-only; look for Z-Image-Edit if available.");
        }

        var origin = LemonadeModelCatalogResolver.NormalizeBaseUrl(_keyStore.LemonadeBaseUrl);
        var url = origin + "/v1/images/edits";

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(model), "model");
            content.Add(new StringContent(request.Prompt.Trim()), "prompt");
            content.Add(new StringContent(string.IsNullOrWhiteSpace(request.Size) ? "512x512" : request.Size.Trim()), "size");
            content.Add(new StringContent("1"), "n");
            content.Add(new StringContent("b64_json"), "response_format");
            if (request.Steps is > 0)
                content.Add(new StringContent(request.Steps.Value.ToString()), "steps");
            if (request.CfgScale is not null)
                content.Add(new StringContent(request.CfgScale.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cfg_scale");
            if (request.Seed is not null)
                content.Add(new StringContent(request.Seed.Value.ToString()), "seed");

            var imageContent = new ByteArrayContent(request.ImagePngBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(imageContent, "image", "source.png");

            if (request.MaskPngBytes is { Length: > 0 })
            {
                var maskContent = new ByteArrayContent(request.MaskPngBytes);
                maskContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                content.Add(maskContent, "mask", "mask.png");
            }

            using var req = LemonadeModelCatalogResolver.CreateRequest(HttpMethod.Post, url, _keyStore.LemonadeApiKey);
            req.Content = content;

            using var resp = await SharedHttp.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return LemonadeImageResult.Fail(FormatHttpError(resp.StatusCode, respText));

            return ParseImageResponse(respText, model);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LemonadeImageResult.Fail("Image edit was cancelled.");
        }
        catch (Exception ex)
        {
            return LemonadeImageResult.Fail(FriendlyNetworkError(ex, "edit"));
        }
    }

    public async Task<LemonadeImageResult> UpscaleAsync(string base64Png, string upscaleModel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(base64Png))
            return LemonadeImageResult.Fail("Image data is required for upscale.");
        if (string.IsNullOrWhiteSpace(upscaleModel) ||
            upscaleModel.Equals("off", StringComparison.OrdinalIgnoreCase))
            return LemonadeImageResult.Fail("No upscale model selected.");

        var b64 = base64Png.Trim();
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            b64 = b64[(comma + 1)..];

        var origin = LemonadeModelCatalogResolver.NormalizeBaseUrl(_keyStore.LemonadeBaseUrl);
        var url = origin + "/v1/images/upscale";
        var body = new Dictionary<string, object?>
        {
            ["image"] = b64,
            ["model"] = upscaleModel.Trim()
        };

        try
        {
            var json = JsonSerializer.Serialize(body);
            using var req = LemonadeModelCatalogResolver.CreateRequest(HttpMethod.Post, url, _keyStore.LemonadeApiKey);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await SharedHttp.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return LemonadeImageResult.Fail(FormatHttpError(resp.StatusCode, respText));

            return ParseImageResponse(respText, upscaleModel.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LemonadeImageResult.Fail("Upscale was cancelled.");
        }
        catch (Exception ex)
        {
            return LemonadeImageResult.Fail(FriendlyNetworkError(ex, "upscale"));
        }
    }

    private static string StripPrefix(string model) =>
        model.StartsWith("lemonade/", StringComparison.OrdinalIgnoreCase)
            ? model.Split('/', 2)[1]
            : model;

    private static LemonadeImageResult ParseImageResponse(string json, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return LemonadeImageResult.Fail("Lemonade returned no image data.");

            var first = data[0];
            string? b64 = null;
            if (first.TryGetProperty("b64_json", out var b64El))
                b64 = b64El.GetString();

            if (string.IsNullOrWhiteSpace(b64))
                return LemonadeImageResult.Fail("Lemonade response missing b64_json image data.");

            var comma = b64.IndexOf(',');
            if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                b64 = b64[(comma + 1)..];

            return new LemonadeImageResult(true, Base64Png: b64, Model: model);
        }
        catch (Exception ex)
        {
            return LemonadeImageResult.Fail("Could not parse Lemonade image response: " + ex.Message);
        }
    }

    private static string FormatHttpError(System.Net.HttpStatusCode status, string body)
    {
        string core;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    core = $"Lemonade error ({(int)status}): {msg.GetString()}";
                else if (err.ValueKind == JsonValueKind.String)
                    core = $"Lemonade error ({(int)status}): {err.GetString()}";
                else
                    core = $"Lemonade error ({(int)status}).";
            }
            else if (doc.RootElement.TryGetProperty("message", out var m))
                core = $"Lemonade error ({(int)status}): {m.GetString()}";
            else
            {
                var snippet = body.Length > 280 ? body[..280] + "…" : body;
                core = $"Lemonade HTTP {(int)status}: {snippet}";
            }
        }
        catch
        {
            var snippet = body.Length > 280 ? body[..280] + "…" : body;
            core = $"Lemonade HTTP {(int)status}: {snippet}";
        }

        if (core.Contains("watchdog", StringComparison.OrdinalIgnoreCase) ||
            core.Contains("sd-server", StringComparison.OrdinalIgnoreCase) ||
            core.Contains("unresponsive", StringComparison.OrdinalIgnoreCase))
        {
            core += "\n\nTip: Lemonade reloaded the image backend. Retry once. " +
                    "For editing, use a model labeled “edit” (e.g. Flux-2-Klein)—generate-only models like Z-Image-Turbo often hang on /images/edits.";
        }

        return core;
    }

    private static string FriendlyNetworkError(Exception ex, string action)
    {
        var msg = ex.Message;
        if (msg.Contains("CORS", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Mixed Content", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
            return $"Could not reach Lemonade to {action} the image (network/CORS/mixed content). " +
                   "On https://wizionic.com set LEMONADE_ALLOWED_ORIGINS, or use the MAUI app against localhost.";
        }

        if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No connection", StringComparison.OrdinalIgnoreCase))
        {
            return $"Could not connect to Lemonade to {action} the image. Is the server running?";
        }

        return $"Image {action} failed: {msg}";
    }
}
