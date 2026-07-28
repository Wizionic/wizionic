using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChatfishApp.Core.Lemonade;
using ChatfishApp.Core.Storage;

namespace ChatfishApp.Shared.Services.Lemonade;

/// <summary>
/// Lemonade speech endpoints: transcription (Whisper) and TTS (Kokoro / OpenMOSS).
/// </summary>
public sealed class LemonadeSpeechService : ILemonadeSpeechService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly IKeyStore _keyStore;

    public LemonadeSpeechService(IKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    public bool IsSttAvailable => SttModelNames.Count > 0;

    public bool IsTtsAvailable => TtsModelNames.Count > 0;

    public string? DefaultSttModel
    {
        get
        {
            var d = _keyStore.LemonadeDefaultSttModel;
            if (!string.IsNullOrWhiteSpace(d) && SttModelNames.Any(n => n.Equals(d, StringComparison.OrdinalIgnoreCase)))
                return d;
            return SttModelNames.FirstOrDefault();
        }
    }

    public string? DefaultTtsModel
    {
        get
        {
            var d = _keyStore.LemonadeDefaultTtsModel;
            if (!string.IsNullOrWhiteSpace(d) && TtsModelNames.Any(n => n.Equals(d, StringComparison.OrdinalIgnoreCase)))
                return d;
            return TtsModelNames.FirstOrDefault();
        }
    }

    public string? DefaultVoice =>
        string.IsNullOrWhiteSpace(_keyStore.LemonadeDefaultVoice)
            ? "shimmer"
            : _keyStore.LemonadeDefaultVoice;

    public IReadOnlyList<string> SttModelNames =>
        _keyStore.LemonadeModelSettingsList
            .Where(m => m.IsTranscription)
            .Select(m => m.Name)
            .ToList();

    public IReadOnlyList<string> TtsModelNames =>
        _keyStore.LemonadeModelSettingsList
            .Where(m => m.IsTts)
            .Select(m => m.Name)
            .ToList();

    public async Task<LemonadeTranscriptionResult> TranscribeAsync(
        LemonadeTranscriptionRequest request,
        CancellationToken ct = default)
    {
        if (request.WavBytes is not { Length: > 0 })
            return LemonadeTranscriptionResult.Fail("No audio data to transcribe.");

        var model = string.IsNullOrWhiteSpace(request.Model) ? DefaultSttModel : request.Model.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return LemonadeTranscriptionResult.Fail(
                "No speech-to-text model configured. Refresh Lemonade models on Local AI and set a default STT model (e.g. Whisper-Tiny).");

        var origin = LemonadeModelCatalogResolver.NormalizeBaseUrl(_keyStore.LemonadeBaseUrl);
        var url = origin + "/v1/audio/transcriptions";

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(model), "model");
            if (!string.IsNullOrWhiteSpace(request.Language))
                content.Add(new StringContent(request.Language.Trim()), "language");
            content.Add(new StringContent("json"), "response_format");

            var fileContent = new ByteArrayContent(request.WavBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "recording.wav" : request.FileName;
            if (!fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                fileName += ".wav";
            content.Add(fileContent, "file", fileName);

            using var req = LemonadeModelCatalogResolver.CreateRequest(HttpMethod.Post, url, _keyStore.LemonadeApiKey);
            req.Content = content;

            using var resp = await SharedHttp.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return LemonadeTranscriptionResult.Fail(FormatHttpError(resp.StatusCode, respText));

            try
            {
                using var doc = JsonDocument.Parse(respText);
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                    return LemonadeTranscriptionResult.Fail("Lemonade returned an empty transcript.");

                return new LemonadeTranscriptionResult(true, Text: text.Trim(), Model: model);
            }
            catch (Exception ex)
            {
                return LemonadeTranscriptionResult.Fail("Could not parse transcription response: " + ex.Message);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LemonadeTranscriptionResult.Fail("Transcription was cancelled.");
        }
        catch (Exception ex)
        {
            return LemonadeTranscriptionResult.Fail(FriendlyNetworkError(ex, "speech-to-text"));
        }
    }

    public async Task<LemonadeSpeechResult> SpeakAsync(
        LemonadeSpeechRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
            return LemonadeSpeechResult.Fail("No text to speak.");

        // Cap extremely long messages to avoid multi-minute waits / server limits.
        var text = request.Input.Trim();
        const int maxChars = 4000;
        if (text.Length > maxChars)
            text = text[..maxChars] + "…";

        var model = string.IsNullOrWhiteSpace(request.Model) ? DefaultTtsModel : request.Model.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return LemonadeSpeechResult.Fail(
                "No text-to-speech model configured. Refresh Lemonade models on Local AI and set a default TTS model (e.g. kokoro-v1).");

        var voice = string.IsNullOrWhiteSpace(request.Voice) ? DefaultVoice : request.Voice.Trim();
        var format = string.IsNullOrWhiteSpace(request.ResponseFormat) ? "mp3" : request.ResponseFormat.Trim().ToLowerInvariant();
        var speed = request.Speed <= 0 ? 1.0 : request.Speed;

        var origin = LemonadeModelCatalogResolver.NormalizeBaseUrl(_keyStore.LemonadeBaseUrl);
        var url = origin + "/v1/audio/speech";

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = text,
            ["voice"] = voice ?? "shimmer",
            ["speed"] = speed,
            ["response_format"] = format
        };

        try
        {
            var json = JsonSerializer.Serialize(body);
            using var req = LemonadeModelCatalogResolver.CreateRequest(HttpMethod.Post, url, _keyStore.LemonadeApiKey);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await SharedHttp.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                return LemonadeSpeechResult.Fail(FormatHttpError(resp.StatusCode, errBody));
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return LemonadeSpeechResult.Fail("Lemonade returned empty audio.");

            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = format switch
                {
                    "wav" => "audio/wav",
                    "opus" => "audio/opus",
                    "pcm" => "audio/pcm",
                    _ => "audio/mpeg"
                };
            }

            return new LemonadeSpeechResult(true, AudioBytes: bytes, ContentType: contentType, Model: model);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LemonadeSpeechResult.Fail("Speech synthesis was cancelled.");
        }
        catch (Exception ex)
        {
            return LemonadeSpeechResult.Fail(FriendlyNetworkError(ex, "text-to-speech"));
        }
    }

    private static string FormatHttpError(System.Net.HttpStatusCode status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    return $"Lemonade error ({(int)status}): {msg.GetString()}";
                if (err.ValueKind == JsonValueKind.String)
                    return $"Lemonade error ({(int)status}): {err.GetString()}";
            }
            if (doc.RootElement.TryGetProperty("message", out var m))
                return $"Lemonade error ({(int)status}): {m.GetString()}";
        }
        catch
        {
            // fall through
        }

        var snippet = body.Length > 240 ? body[..240] + "…" : body;
        return $"Lemonade HTTP {(int)status}: {snippet}";
    }

    private static string FriendlyNetworkError(Exception ex, string feature)
    {
        var msg = ex.Message;
        if (msg.Contains("CORS", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Mixed Content", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
            return $"Could not reach Lemonade for {feature} (network/CORS/mixed content). " +
                   "On https://chatfish.me set LEMONADE_ALLOWED_ORIGINS, or use the MAUI app.";
        }

        if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return $"Could not connect to Lemonade for {feature}. Is the server running?";

        return $"{feature} failed: {msg}";
    }
}
