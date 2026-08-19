using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using App.Core.Cloud;
using App.Core.Storage;

namespace App.Shared.Services.Cloud;

public sealed class CloudSpeechService : ICloudSpeechService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly IKeyStore _keyStore;

    public CloudSpeechService(IKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    public bool IsSttAvailable(string providerId)
    {
        var p = _keyStore.GetCloudProvider(providerId);
        if (p == null) return false;
        return p.HasXaiStt || p.HasOpenAiAudio || p.Models.Any(m => m.IsTranscription);
    }

    public bool IsTtsAvailable(string providerId)
    {
        var p = _keyStore.GetCloudProvider(providerId);
        if (p == null) return false;
        return p.HasXaiTts || p.HasOpenAiAudio || p.Models.Any(m => m.IsTts);
    }

    public string? DefaultSttModel(string providerId)
    {
        var p = _keyStore.GetCloudProvider(providerId);
        if (p == null) return null;
        var names = SttModelNames(providerId);
        if (!string.IsNullOrWhiteSpace(p.DefaultSttModel) &&
            names.Any(n => n.Equals(p.DefaultSttModel, StringComparison.OrdinalIgnoreCase)))
            return p.DefaultSttModel;
        return names.FirstOrDefault();
    }

    public string? DefaultTtsModel(string providerId)
    {
        var p = _keyStore.GetCloudProvider(providerId);
        if (p == null) return null;
        var names = TtsModelNames(providerId);
        if (!string.IsNullOrWhiteSpace(p.DefaultTtsModel) &&
            names.Any(n => n.Equals(p.DefaultTtsModel, StringComparison.OrdinalIgnoreCase)))
            return p.DefaultTtsModel;
        return names.FirstOrDefault();
    }

    public string? DefaultVoice(string providerId)
    {
        var p = _keyStore.GetCloudProvider(providerId);
        if (p == null) return null;
        if (!string.IsNullOrWhiteSpace(p.DefaultVoice))
            return p.DefaultVoice;
        return p.Voices.FirstOrDefault()?.VoiceId;
    }

    public IReadOnlyList<string> SttModelNames(string providerId) =>
        _keyStore.GetCloudProvider(providerId)?.Models
            .Where(m => m.IsTranscription)
            .Select(m => m.Name)
            .ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();

    public IReadOnlyList<string> TtsModelNames(string providerId) =>
        _keyStore.GetCloudProvider(providerId)?.Models
            .Where(m => m.IsTts)
            .Select(m => m.Name)
            .ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();

    public IReadOnlyList<(string Id, string Name)> Voices(string providerId) =>
        _keyStore.GetCloudProvider(providerId)?.Voices
            .Select(v => (v.VoiceId, string.IsNullOrWhiteSpace(v.Name) ? v.VoiceId : v.Name))
            .ToList()
        ?? (IReadOnlyList<(string, string)>)Array.Empty<(string, string)>();

    public async Task<CloudTranscriptionResult> TranscribeAsync(
        CloudTranscriptionRequest request,
        CancellationToken ct = default)
    {
        if (request.WavBytes is not { Length: > 0 })
            return CloudTranscriptionResult.Fail("No audio data to transcribe.");

        var provider = _keyStore.GetCloudProvider(request.ProviderId);
        if (provider == null)
            return CloudTranscriptionResult.Fail("Cloud provider not found.");

        var origin = CloudModelCatalogResolver.NormalizeBaseUrl(provider.BaseUrl);
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? DefaultSttModel(request.ProviderId)
            : request.Model.Trim();
        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "recording.wav" : request.FileName;
        if (!fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            fileName += ".wav";

        try
        {
            if (!provider.HasXaiStt || !string.IsNullOrWhiteSpace(model))
            {
                var openAi = await PostOpenAiTranscriptionAsync(
                    origin, provider.ApiKey, model, request.Language, request.WavBytes, fileName, ct);
                if (openAi.Success)
                    return openAi;
                if (!provider.HasXaiStt)
                    return openAi;
            }

            return await PostXaiSttAsync(origin, provider.ApiKey, request.Language, request.WavBytes, fileName, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CloudTranscriptionResult.Fail("Transcription was cancelled.");
        }
        catch (Exception ex)
        {
            return CloudTranscriptionResult.Fail("Speech-to-text failed: " + ex.Message);
        }
    }

    public async Task<CloudSpeechResult> SpeakAsync(CloudSpeechRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
            return CloudSpeechResult.Fail("No text to speak.");

        var provider = _keyStore.GetCloudProvider(request.ProviderId);
        if (provider == null)
            return CloudSpeechResult.Fail("Cloud provider not found.");

        var text = request.Input.Trim();
        if (text.Length > 4000)
            text = text[..4000] + "…";

        var origin = CloudModelCatalogResolver.NormalizeBaseUrl(provider.BaseUrl);
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? DefaultTtsModel(request.ProviderId)
            : request.Model.Trim();
        var voice = string.IsNullOrWhiteSpace(request.Voice)
            ? DefaultVoice(request.ProviderId)
            : request.Voice.Trim();
        var format = string.IsNullOrWhiteSpace(request.ResponseFormat)
            ? "mp3"
            : request.ResponseFormat.Trim().ToLowerInvariant();

        try
        {
            if (provider.HasXaiTts && string.IsNullOrWhiteSpace(model))
            {
                return await PostXaiTtsAsync(origin, provider.ApiKey, text, voice, ct);
            }

            if (!string.IsNullOrWhiteSpace(model) || provider.HasOpenAiAudio)
            {
                var openAi = await PostOpenAiSpeechAsync(origin, provider.ApiKey, text, model, voice, format, request.Speed, ct);
                if (openAi.Success)
                    return openAi;
                if (provider.HasXaiTts)
                    return await PostXaiTtsAsync(origin, provider.ApiKey, text, voice, ct);
                return openAi;
            }

            if (provider.HasXaiTts)
                return await PostXaiTtsAsync(origin, provider.ApiKey, text, voice, ct);

            return CloudSpeechResult.Fail("No text-to-speech endpoint discovered for this provider.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CloudSpeechResult.Fail("Speech synthesis was cancelled.");
        }
        catch (Exception ex)
        {
            return CloudSpeechResult.Fail("Text-to-speech failed: " + ex.Message);
        }
    }

    private static async Task<CloudTranscriptionResult> PostOpenAiTranscriptionAsync(
        string origin, string apiKey, string? model, string? language, byte[] wav, string fileName, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(model))
            content.Add(new StringContent(model), "model");
        if (!string.IsNullOrWhiteSpace(language))
            content.Add(new StringContent(language.Trim()), "language");
        content.Add(new StringContent("json"), "response_format");
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", fileName);

        using var req = CloudModelCatalogResolver.CreateRequest(HttpMethod.Post, origin + "/audio/transcriptions", apiKey);
        req.Content = content;
        using var resp = await SharedHttp.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return CloudTranscriptionResult.Fail(CloudModelCatalogResolver.FormatHttpError(resp.StatusCode, respText));

        return ParseTranscriptJson(respText, model);
    }

    private static async Task<CloudTranscriptionResult> PostXaiSttAsync(
        string origin, string apiKey, string? language, byte[] wav, string fileName, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(language))
            content.Add(new StringContent(language.Trim()), "language");
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", fileName);

        using var req = CloudModelCatalogResolver.CreateRequest(HttpMethod.Post, origin + "/stt", apiKey);
        req.Content = content;
        using var resp = await SharedHttp.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return CloudTranscriptionResult.Fail(CloudModelCatalogResolver.FormatHttpError(resp.StatusCode, respText));

        return ParseTranscriptJson(respText, "stt");
    }

    private static CloudTranscriptionResult ParseTranscriptJson(string json, string? model)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
                return CloudTranscriptionResult.Fail("Provider returned an empty transcript.");
            return new CloudTranscriptionResult(true, Text: text.Trim(), Model: model);
        }
        catch (Exception ex)
        {
            return CloudTranscriptionResult.Fail("Could not parse transcription response: " + ex.Message);
        }
    }

    private static async Task<CloudSpeechResult> PostOpenAiSpeechAsync(
        string origin, string apiKey, string text, string? model, string? voice, string format, double speed, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["input"] = text,
            ["voice"] = voice ?? "alloy",
            ["speed"] = speed <= 0 ? 1.0 : speed,
            ["response_format"] = format
        };
        if (!string.IsNullOrWhiteSpace(model))
            body["model"] = model;

        using var req = CloudModelCatalogResolver.CreateRequest(HttpMethod.Post, origin + "/audio/speech", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await SharedHttp.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return CloudSpeechResult.Fail(CloudModelCatalogResolver.FormatHttpError(resp.StatusCode, err));
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0)
            return CloudSpeechResult.Fail("Provider returned empty audio.");

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = format == "wav" ? "audio/wav" : "audio/mpeg";

        return new CloudSpeechResult(true, AudioBytes: bytes, ContentType: contentType, Model: model);
    }

    private static async Task<CloudSpeechResult> PostXaiTtsAsync(
        string origin, string apiKey, string text, string? voice, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["language"] = "auto"
        };
        if (!string.IsNullOrWhiteSpace(voice))
            body["voice_id"] = voice;

        using var req = CloudModelCatalogResolver.CreateRequest(HttpMethod.Post, origin + "/tts", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await SharedHttp.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return CloudSpeechResult.Fail(CloudModelCatalogResolver.FormatHttpError(resp.StatusCode, err));
        }

        var media = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (media.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            var raw = await resp.Content.ReadAsByteArrayAsync(ct);
            if (raw.Length == 0)
                return CloudSpeechResult.Fail("Provider returned empty audio.");
            return new CloudSpeechResult(true, AudioBytes: raw, ContentType: media.StartsWith("audio/") ? media : "audio/mpeg", Model: "tts");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var b64 = doc.RootElement.TryGetProperty("audio", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(b64))
                return CloudSpeechResult.Fail("Provider returned no audio.");
            var bytes = Convert.FromBase64String(b64);
            var ctype = doc.RootElement.TryGetProperty("content_type", out var ctEl)
                ? ctEl.GetString()
                : "audio/mpeg";
            return new CloudSpeechResult(true, AudioBytes: bytes, ContentType: ctype ?? "audio/mpeg", Model: "tts");
        }
        catch (Exception ex)
        {
            return CloudSpeechResult.Fail("Could not parse TTS response: " + ex.Message);
        }
    }
}
