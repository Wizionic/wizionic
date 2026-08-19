using System.ComponentModel;
using App.Core.Cloud;
using App.Core.Storage;
using App.Core.Tools;
using Microsoft.Extensions.AI;

namespace App.Shared.Services.Tools;

/// <summary>
/// Image tools for the currently selected user-keyed cloud provider.
/// Used when Chat has a <c>cloud/{provider}/…</c> model selected and that
/// provider has image generate/edit models (e.g. grok-imagine-image).
/// </summary>
public sealed class CloudToolModule : IToolModule
{
    private readonly IKeyStore _keyStore;
    private readonly ICloudImageService _images;
    private readonly IConversationMediaBuffer _media;
    private readonly IToolConversationContext _convoCtx;
    private readonly IToolExecutionTrace _trace;

    public CloudToolModule(
        IKeyStore keyStore,
        ICloudImageService images,
        IConversationMediaBuffer media,
        IToolConversationContext convoCtx,
        IToolExecutionTrace trace)
    {
        _keyStore = keyStore;
        _images = images;
        _media = media;
        _convoCtx = convoCtx;
        _trace = trace;
    }

    public string ModuleName => "Cloud";

    public bool IsAvailable => TryCurrentProvider(out var providerId) &&
                               ResolveImageModel(providerId, edit: false) != null;

    public IReadOnlyList<AITool> GetTools()
    {
        if (!TryCurrentProvider(out var providerId))
            return Array.Empty<AITool>();

        var tools = new List<AITool>();
        if (ResolveImageModel(providerId, edit: false) != null)
        {
            tools.Add(AIFunctionFactory.Create(GenerateImageAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "generate_image",
                    Description =
                        "Generate an image with the cloud provider of the selected chat model " +
                        "(e.g. xAI grok-imagine-image). Use this when the user asks to draw, illustrate, or generate a picture. " +
                        "Do not use Lemonade or any local image tool. " +
                        "Returns a short generation_id only (the image is shown automatically). " +
                        "If they also asked to save to a gallery/album, call save_to_gallery with that generation_id."
                }));
        }

        if (ResolveImageModel(providerId, edit: true) != null)
        {
            tools.Add(AIFunctionFactory.Create(EditImageAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "edit_image",
                    Description =
                        "Edit an existing image with the cloud provider of the selected chat model. " +
                        "Pass the source as base64 (no data: prefix). " +
                        "Returns a short generation_id only; use save_to_gallery to persist."
                }));
        }

        return tools;
    }

    [Description("Generate an image from a text prompt using the selected cloud provider.")]
    private async Task<string> GenerateImageAsync(
        [Description("Detailed description of the image to generate.")] string prompt)
    {
        if (!TryCurrentProvider(out var providerId))
            return "Image generation failed: no cloud provider selected.";

        var model = ResolveImageModel(providerId, edit: false);
        var label = Describe(providerId, model);
        _trace.Record($"🎨 generate_image({label})");

        var result = await _images.GenerateAsync(new CloudImageGenerateRequest(
            ProviderId: providerId,
            Prompt: prompt,
            Model: model));

        if (!result.Success || string.IsNullOrWhiteSpace(result.Base64Png))
        {
            var err = result.Error ?? "unknown error";
            _trace.Record("   ❌ " + err);
            return "Image generation failed: " + err;
        }

        var used = result.Model ?? model ?? "image";
        var genId = BufferImage(result.Base64Png, "generated-image.png", "cloud_generate:" + used);
        _trace.Record($"   ✅ {label} → generation_id={genId}");
        return
            $"OK: image generated with {label}. generation_id={genId}. " +
            "The image is displayed to the user (do not re-output image data). " +
            $"If they asked to save it, call save_to_gallery(album_name=…, generation_id={genId}).";
    }

    [Description("Edit an image with a text instruction using the selected cloud provider.")]
    private async Task<string> EditImageAsync(
        [Description("What to change in the image.")] string prompt,
        [Description("Source image as raw base64 (no data URL prefix).")] string imageBase64)
    {
        if (!TryCurrentProvider(out var providerId))
            return "Image edit failed: no cloud provider selected.";

        byte[] bytes;
        try
        {
            var b64 = imageBase64.Trim();
            var comma = b64.IndexOf(',');
            if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                b64 = b64[(comma + 1)..];
            bytes = Convert.FromBase64String(b64);
        }
        catch
        {
            return "Edit failed: invalid image base64.";
        }

        var model = ResolveImageModel(providerId, edit: true);
        var label = Describe(providerId, model);
        _trace.Record($"🎨 edit_image({label})");

        var result = await _images.EditAsync(new CloudImageEditRequest(
            ProviderId: providerId,
            Prompt: prompt,
            ImageBytes: bytes,
            Model: model));

        if (!result.Success || string.IsNullOrWhiteSpace(result.Base64Png))
        {
            var err = result.Error ?? "unknown error";
            _trace.Record("   ❌ " + err);
            return "Image edit failed: " + err;
        }

        var used = result.Model ?? model ?? "image";
        var genId = BufferImage(result.Base64Png, "edited-image.png", "cloud_edit:" + used);
        _trace.Record($"   ✅ {label} → generation_id={genId}");
        return
            $"OK: image edited with {label}. generation_id={genId}. " +
            "The image is displayed to the user (do not re-output image data). " +
            $"If they asked to save it, call save_to_gallery(album_name=…, generation_id={genId}).";
    }

    private bool TryCurrentProvider(out string providerId)
    {
        providerId = "";
        var imageId = ModelProfileId.ResolveImageModelId(_keyStore);
        if (!ModelProfileId.TryCloudProvider(imageId, out providerId, out _))
            return false;
        return _keyStore.GetCloudProvider(providerId) != null;
    }

    private string? ResolveImageModel(string providerId, bool edit)
    {
        var slot = edit
            ? ModelProfileId.ResolveEditModelId(_keyStore)
            : ModelProfileId.ResolveImageModelId(_keyStore);
        if (ModelProfileId.TryCloudProvider(slot, out var slotPid, out var slotName)
            && slotPid.Equals(providerId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(slotName))
            return slotName;

        var preferred = edit
            ? _images.DefaultEditModel(providerId) ?? _images.DefaultImageModel(providerId)
            : _images.DefaultImageModel(providerId);
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;

        var models = _keyStore.GetCloudProvider(providerId)?.Models;
        if (models == null)
            return null;

        return models.FirstOrDefault(m => edit ? m.IsEdit : (m.IsImage || m.IsEdit))?.Name
               ?? models.FirstOrDefault(m =>
                   m.Name.Contains("imagine", StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private string Describe(string providerId, string? model)
    {
        var name = _keyStore.GetCloudProvider(providerId)?.DisplayName ?? providerId;
        var m = string.IsNullOrWhiteSpace(model) ? "(default)" : model;
        return $"{name} · {m}";
    }

    private string BufferImage(string base64, string name, string source)
    {
        var convoId = _convoCtx.ConversationId ?? "_default";
        return _media.AddImage(convoId, base64, "image/png", name, source);
    }
}
