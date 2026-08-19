namespace App.Core.Cloud;

public interface ICloudSpeechService
{
    bool IsSttAvailable(string providerId);
    bool IsTtsAvailable(string providerId);
    string? DefaultSttModel(string providerId);
    string? DefaultTtsModel(string providerId);
    string? DefaultVoice(string providerId);
    IReadOnlyList<string> SttModelNames(string providerId);
    IReadOnlyList<string> TtsModelNames(string providerId);
    IReadOnlyList<(string Id, string Name)> Voices(string providerId);

    Task<CloudTranscriptionResult> TranscribeAsync(CloudTranscriptionRequest request, CancellationToken ct = default);
    Task<CloudSpeechResult> SpeakAsync(CloudSpeechRequest request, CancellationToken ct = default);
}

public sealed record CloudTranscriptionRequest(
    string ProviderId,
    byte[] WavBytes,
    string? Model = null,
    string? Language = null,
    string FileName = "recording.wav");

public sealed record CloudTranscriptionResult(
    bool Success,
    string? Text = null,
    string? Model = null,
    string? Error = null)
{
    public static CloudTranscriptionResult Fail(string error) => new(false, Error: error);
}

public sealed record CloudSpeechRequest(
    string ProviderId,
    string Input,
    string? Model = null,
    string? Voice = null,
    double Speed = 1.0,
    string ResponseFormat = "mp3");

public sealed record CloudSpeechResult(
    bool Success,
    byte[]? AudioBytes = null,
    string? ContentType = null,
    string? Model = null,
    string? Error = null)
{
    public static CloudSpeechResult Fail(string error) => new(false, Error: error);

    public string? ToBase64() =>
        AudioBytes is { Length: > 0 } ? Convert.ToBase64String(AudioBytes) : null;
}
