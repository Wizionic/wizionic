using App.Core.Cloud;
using App.Core.Lemonade;
using App.Core.Speech;
using App.Core.Storage;

namespace App.Shared.Services.Speech;

public sealed class SpeechTranscriptionService : ISpeechTranscriptionService
{
    private readonly IKeyStore _keys;
    private readonly ILemonadeSpeechService _lemonade;
    private readonly ICloudSpeechService _cloud;

    public SpeechTranscriptionService(
        IKeyStore keys,
        ILemonadeSpeechService lemonade,
        ICloudSpeechService cloud)
    {
        _keys = keys;
        _lemonade = lemonade;
        _cloud = cloud;
    }

    public bool IsNotesSttAvailable
    {
        get
        {
            var id = ResolveNotesSttModelId();
            return !string.IsNullOrWhiteSpace(id) && IsAvailable(id);
        }
    }

    public string? NotesSttModelHint
    {
        get
        {
            var id = ResolveNotesSttModelId();
            if (string.IsNullOrWhiteSpace(id))
                return null;
            if (ModelProfileId.TryCloudProvider(id, out var pid, out var name))
                return name ?? _cloud.DefaultSttModel(pid) ?? "STT";
            if (ModelProfileId.IsLemonadeCatalog(id))
            {
                var parts = id.Split('/', 2);
                return parts.Length == 2 ? parts[1] : id;
            }
            return id;
        }
    }

    public string? ResolveNotesSttModelId()
    {
        var explicitId = _keys.GetUserProfile().NotesSttModelId;
        if (!string.IsNullOrWhiteSpace(explicitId) && IsAvailable(explicitId))
            return explicitId.Trim();

        if (_lemonade.IsSttAvailable && !string.IsNullOrWhiteSpace(_lemonade.DefaultSttModel))
            return "lemonade/" + _lemonade.DefaultSttModel;

        foreach (var p in _keys.CloudProviders)
        {
            if (_cloud.IsSttAvailable(p.Id))
                return "cloud/" + p.Id;
        }

        return null;
    }

    public async Task<SpeechTranscriptionResult> TranscribeAsync(
        byte[] wavBytes,
        string? catalogModelId,
        string fileName = "recording.wav",
        CancellationToken ct = default)
    {
        if (wavBytes is not { Length: > 0 })
            return SpeechTranscriptionResult.Fail("No audio data to transcribe.");

        var id = string.IsNullOrWhiteSpace(catalogModelId) ? ResolveNotesSttModelId() : catalogModelId.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return SpeechTranscriptionResult.Fail(
                "No speech-to-text model configured. Pick one under Settings → Voice, or refresh Lemonade / cloud STT models.");

        if (ModelProfileId.TryCloudProvider(id, out var pid, out var modelName))
        {
            var result = await _cloud.TranscribeAsync(new CloudTranscriptionRequest(
                ProviderId: pid,
                WavBytes: wavBytes,
                Model: modelName ?? _cloud.DefaultSttModel(pid),
                FileName: fileName), ct);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Text))
                return SpeechTranscriptionResult.Fail(result.Error ?? "Transcription failed.");
            return new SpeechTranscriptionResult(true, result.Text.Trim(), result.Model);
        }

        if (ModelProfileId.IsLemonadeCatalog(id))
        {
            var lemonadeName = id.Split('/', 2) is { Length: 2 } parts ? parts[1] : _lemonade.DefaultSttModel;
            var result = await _lemonade.TranscribeAsync(new LemonadeTranscriptionRequest(
                WavBytes: wavBytes,
                Model: lemonadeName,
                FileName: fileName), ct);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Text))
                return SpeechTranscriptionResult.Fail(result.Error ?? "Transcription failed.");
            return new SpeechTranscriptionResult(true, result.Text.Trim(), result.Model);
        }

        return SpeechTranscriptionResult.Fail("Unknown speech-to-text model: " + id);
    }

    private bool IsAvailable(string catalogId)
    {
        if (ModelProfileId.TryCloudProvider(catalogId, out var pid, out _))
            return _cloud.IsSttAvailable(pid);
        if (ModelProfileId.IsLemonadeCatalog(catalogId))
            return _lemonade.IsSttAvailable;
        return false;
    }
}
