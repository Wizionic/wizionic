namespace App.Core.Lemonade;

/// <summary>
/// Client for Lemonade speech-to-text (<c>POST /v1/audio/transcriptions</c>)
/// and text-to-speech (<c>POST /v1/audio/speech</c>).
/// </summary>
public interface ILemonadeSpeechService
{
    bool IsSttAvailable { get; }
    bool IsTtsAvailable { get; }

    string? DefaultSttModel { get; }
    string? DefaultTtsModel { get; }
    string? DefaultVoice { get; }

    IReadOnlyList<string> SttModelNames { get; }
    IReadOnlyList<string> TtsModelNames { get; }

    /// <summary>
    /// Transcribe WAV audio via Whisper. Lemonade currently accepts WAV only.
    /// </summary>
    Task<LemonadeTranscriptionResult> TranscribeAsync(
        LemonadeTranscriptionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Synthesize speech via Kokoro / OpenMOSS. Returns raw audio bytes (mp3/wav).
    /// </summary>
    Task<LemonadeSpeechResult> SpeakAsync(
        LemonadeSpeechRequest request,
        CancellationToken ct = default);
}

public sealed record LemonadeTranscriptionRequest(
    byte[] WavBytes,
    string? Model = null,
    string? Language = null,
    string FileName = "recording.wav");

public sealed record LemonadeTranscriptionResult(
    bool Success,
    string? Text = null,
    string? Model = null,
    string? Error = null)
{
    public static LemonadeTranscriptionResult Fail(string error) => new(false, Error: error);
}

public sealed record LemonadeSpeechRequest(
    string Input,
    string? Model = null,
    string? Voice = null,
    double Speed = 1.0,
    string ResponseFormat = "mp3");

public sealed record LemonadeSpeechResult(
    bool Success,
    byte[]? AudioBytes = null,
    string? ContentType = null,
    string? Model = null,
    string? Error = null)
{
    public static LemonadeSpeechResult Fail(string error) => new(false, Error: error);

    public string? ToBase64() =>
        AudioBytes is { Length: > 0 } ? Convert.ToBase64String(AudioBytes) : null;
}
