using System.ComponentModel;
using App.Core.Lemonade;
using App.Core.Tools;
using Microsoft.Extensions.AI;

namespace App.Shared.Services.Tools;

/// <summary>
/// Client-side OmniRouter-style tools that call Lemonade modality endpoints.
/// Available when Lemonade image/TTS models are configured and the selected chat
/// model is not a server-side Omni collection (those run tools on the server).
/// </summary>
public sealed class LemonadeToolModule : IToolModule
{
    private readonly ILemonadeImageService _images;
    private readonly ILemonadeSpeechService _speech;
    private readonly IConversationMediaBuffer _media;
    private readonly IToolConversationContext _convoCtx;

    public LemonadeToolModule(
        ILemonadeImageService images,
        ILemonadeSpeechService speech,
        IConversationMediaBuffer media,
        IToolConversationContext convoCtx)
    {
        _images = images;
        _speech = speech;
        _media = media;
        _convoCtx = convoCtx;
    }

    public string ModuleName => "Lemonade";

    public bool IsAvailable => _images.IsGenerateAvailable || _images.IsEditAvailable || _speech.IsTtsAvailable;

    public IReadOnlyList<AITool> GetTools()
    {
        var tools = new List<AITool>();

        if (_images.IsGenerateAvailable)
        {
            tools.Add(AIFunctionFactory.Create(GenerateImageAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "lemonade_generate_image",
                    Description =
                        "Generate an image locally via Lemonade (Stable Diffusion). " +
                        "Use when the user asks you to draw, illustrate, or generate a picture. " +
                        "Returns a short generation_id only (the image is shown to the user automatically). " +
                        "If the user also asked to save to a gallery/album, call save_to_gallery with that generation_id."
                }));
        }

        if (_images.IsEditAvailable)
        {
            tools.Add(AIFunctionFactory.Create(EditImageAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "lemonade_edit_image",
                    Description =
                        "Edit an existing image with Lemonade using an instruction prompt. " +
                        "Pass the source image as base64 PNG (no data: prefix). " +
                        "Only works with edit-capable models (e.g. Flux-2-Klein). " +
                        "Returns a short generation_id only (image is shown automatically); use save_to_gallery to persist."
                }));
        }

        if (_speech.IsTtsAvailable)
        {
            tools.Add(AIFunctionFactory.Create(TextToSpeechAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "lemonade_text_to_speech",
                    Description =
                        "Convert text to speech with Lemonade TTS (e.g. Kokoro). " +
                        "Use when the user asks you to speak, read aloud, or produce audio. " +
                        "Returns an HTML audio data-URI the client can play."
                }));
        }

        return tools;
    }

    [Description("Generate an image from a text prompt using the local Lemonade server.")]
    private async Task<string> GenerateImageAsync(
        [Description("Detailed description of the image to generate.")] string prompt,
        [Description("Optional size like 512x512 or 1024x1024.")] string? size = null,
        [Description("Optional inference steps (turbo models often use 4–9).")] int? steps = null)
    {
        var result = await _images.GenerateAsync(new LemonadeImageGenerateRequest(
            Prompt: prompt,
            Model: _images.DefaultImageModel,
            Size: string.IsNullOrWhiteSpace(size) ? "512x512" : size!,
            Steps: steps));

        if (!result.Success || string.IsNullOrWhiteSpace(result.Base64Png))
            return "Image generation failed: " + (result.Error ?? "unknown error");

        // Never return multi-MB base64 into the tool loop — small models OOM / crash the app.
        // ChatCompletionService attaches the buffered image to the UI result after the turn.
        var genId = BufferImage(result.Base64Png, "image/png", "generated-image.png", "lemonade_generate");
        return
            $"OK: image generated. generation_id={genId}. " +
            "The image is displayed to the user (do not re-output image data). " +
            $"If they asked to save it, call save_to_gallery(album_name=…, generation_id={genId}).";
    }

    [Description("Edit an image with a text instruction using Lemonade.")]
    private async Task<string> EditImageAsync(
        [Description("What to change in the image.")] string prompt,
        [Description("Source image as raw base64 PNG (no data URL prefix).")] string imageBase64,
        [Description("Optional size like 512x512.")] string? size = null)
    {
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

        var result = await _images.EditAsync(new LemonadeImageEditRequest(
            Prompt: prompt,
            ImagePngBytes: bytes,
            Model: _images.DefaultEditModel,
            Size: string.IsNullOrWhiteSpace(size) ? "512x512" : size!));

        if (!result.Success || string.IsNullOrWhiteSpace(result.Base64Png))
            return "Image edit failed: " + (result.Error ?? "unknown error");

        var genId = BufferImage(result.Base64Png, "image/png", "edited-image.png", "lemonade_edit");
        return
            $"OK: image edited. generation_id={genId}. " +
            "The image is displayed to the user (do not re-output image data). " +
            $"If they asked to save it, call save_to_gallery(album_name=…, generation_id={genId}).";
    }

    [Description("Speak text using Lemonade text-to-speech.")]
    private async Task<string> TextToSpeechAsync(
        [Description("The text to speak aloud.")] string text,
        [Description("Optional voice name (e.g. shimmer, af_sky).")] string? voice = null)
    {
        var result = await _speech.SpeakAsync(new LemonadeSpeechRequest(
            Input: text,
            Model: _speech.DefaultTtsModel,
            Voice: voice ?? _speech.DefaultVoice,
            ResponseFormat: "mp3"));

        if (!result.Success || result.AudioBytes is not { Length: > 0 })
            return "Text-to-speech failed: " + (result.Error ?? "unknown error");

        var b64 = Convert.ToBase64String(result.AudioBytes);
        return $"<audio>data:audio/mpeg;base64,{b64}</audio>";
    }

    private string BufferImage(string base64, string contentType, string name, string source)
    {
        var convoId = _convoCtx.ConversationId ?? "_default";
        return _media.AddImage(convoId, base64, contentType, name, source);
    }
}
