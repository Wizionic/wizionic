namespace App.Core.Speech;

/// <summary>
/// Routes WAV transcription to Lemonade or a cloud speech provider from a catalog id.
/// </summary>
public interface ISpeechTranscriptionService
{
    bool IsNotesSttAvailable { get; }
    string? NotesSttModelHint { get; }
    string? ResolveNotesSttModelId();

    Task<SpeechTranscriptionResult> TranscribeAsync(
        byte[] wavBytes,
        string? catalogModelId,
        string fileName = "recording.wav",
        CancellationToken ct = default);
}

public sealed record SpeechTranscriptionResult(
    bool Success,
    string? Text = null,
    string? Model = null,
    string? Error = null)
{
    public static SpeechTranscriptionResult Fail(string error) => new(false, Error: error);
}
