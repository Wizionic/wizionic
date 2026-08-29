using System.Text.Json;
using App.Core.Auth;
using App.Core.Storage;
using Microsoft.JSInterop;

namespace App.Maui.Services;

public sealed class SqliteNoteAudioStore : INoteAudioStore
{
    private const string Prefix = "n-audio-";
    private const string BinV1Prefix = "BIN1:";

    private readonly IAuthService _auth;
    private readonly MauiCryptoService _crypto;
    private readonly SqliteHistoryDatabase _db;
    private readonly IJSRuntime _js;

    public SqliteNoteAudioStore(
        IAuthService auth,
        MauiCryptoService crypto,
        SqliteHistoryDatabase db,
        IJSRuntime js)
    {
        _auth = auth;
        _crypto = crypto;
        _db = db;
        _js = js;
    }

    public bool IsSupported => true;

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);
    private string MetaKey(string ns, string clipId) => ns + Prefix + clipId;
    private string ContentKey(string ns, string clipId) => ns + Prefix + "c-" + clipId;

    public async Task<NoteAudioRef> SaveAsync(
        string notebookId,
        string entryId,
        string clipId,
        string contentType,
        byte[] bytes,
        long durationMs,
        IReadOnlyList<NoteAudioCue>? cues = null,
        CancellationToken ct = default)
    {
        if (bytes is not { Length: > 0 })
            throw new ArgumentException("Audio bytes are required.", nameof(bytes));

        var ns = GetPrefix();
        var keyB64 = await _auth.GetOrCreateHistoryEncryptionKeyAsync();
        var cipher = string.IsNullOrEmpty(keyB64) ? bytes : _crypto.EncryptBytes(keyB64, bytes);
        var packed = BinV1Prefix + Convert.ToBase64String(cipher);
        var now = DateTime.UtcNow;
        var cuesJson = cues is { Count: > 0 } ? JsonSerializer.Serialize(cues) : null;

        await _db.UpsertNoteAudioMetaAsync(new SqliteHistoryDatabase.NoteAudioMetaRow(
            MetaKey(ns, clipId),
            clipId,
            notebookId,
            entryId,
            ns,
            string.IsNullOrWhiteSpace(contentType) ? "audio/webm" : contentType,
            bytes.LongLength,
            durationMs,
            now.ToString("O"),
            cuesJson), ct);
        await _db.PutNoteAudioContentAsync(ContentKey(ns, clipId), packed, ct);

        return new NoteAudioRef(
            clipId,
            durationMs,
            string.IsNullOrWhiteSpace(contentType) ? "audio/webm" : contentType,
            bytes.LongLength,
            now,
            cues?.ToList());
    }

    public async Task<NoteAudioRef?> GetMetaAsync(string clipId, CancellationToken ct = default)
    {
        var row = await _db.GetNoteAudioMetaByIdAsync(GetPrefix(), clipId, ct);
        return row == null ? null : ToRef(row);
    }

    public async Task<bool> ExistsAsync(string clipId, CancellationToken ct = default)
    {
        var content = await _db.GetNoteAudioContentAsync(ContentKey(GetPrefix(), clipId), ct);
        return !string.IsNullOrEmpty(content);
    }

    public async Task<byte[]?> LoadBytesAsync(string clipId, CancellationToken ct = default)
    {
        var encrypted = await _db.GetNoteAudioContentAsync(ContentKey(GetPrefix(), clipId), ct);
        if (string.IsNullOrEmpty(encrypted))
            return null;

        byte[] cipher;
        if (encrypted.StartsWith(BinV1Prefix, StringComparison.Ordinal))
            cipher = Convert.FromBase64String(encrypted[BinV1Prefix.Length..]);
        else
            cipher = Convert.FromBase64String(encrypted);

        var keyB64 = await _auth.GetOrCreateHistoryEncryptionKeyAsync();
        if (string.IsNullOrEmpty(keyB64))
            return cipher;
        return _crypto.DecryptBytes(keyB64, cipher);
    }

    public async Task<string?> CreateDisplayUrlAsync(string clipId, CancellationToken ct = default)
    {
        try
        {
            var bytes = await LoadBytesAsync(clipId, ct);
            if (bytes is not { Length: > 0 })
                return null;
            var meta = await GetMetaAsync(clipId, ct);
            var mime = meta?.ContentType ?? "audio/webm";
            return await _js.InvokeAsync<string?>("galleryObjectUrlFromBase64", Convert.ToBase64String(bytes), mime);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NoteAudio] CreateDisplayUrl failed: {ex.Message}");
            return null;
        }
    }

    public async Task RevokeDisplayUrlAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { await _js.InvokeVoidAsync("galleryRevokeObjectUrl", url); }
        catch { /* ignore */ }
    }

    public async Task DeleteAsync(string clipId, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        await _db.DeleteNoteAudioAsync(MetaKey(ns, clipId), ContentKey(ns, clipId), ct);
    }

    public async Task<int> DeleteByNotebookAsync(string notebookId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(notebookId))
            return 0;
        var ids = await _db.ListNoteAudioIdsByNotebookAsync(GetPrefix(), notebookId, ct);
        foreach (var id in ids)
            await DeleteAsync(id, ct);
        return ids.Count;
    }

    public Task<long> SumStoredBytesAsync(CancellationToken ct = default) =>
        _db.SumContentLengthAsync("note_audio_content", ct);

    private static NoteAudioRef ToRef(SqliteHistoryDatabase.NoteAudioMetaRow row)
    {
        List<NoteAudioCue>? cues = null;
        if (!string.IsNullOrWhiteSpace(row.CuesJson))
        {
            try { cues = JsonSerializer.Deserialize<List<NoteAudioCue>>(row.CuesJson); }
            catch { /* ignore */ }
        }

        DateTime created = DateTime.UtcNow;
        DateTime.TryParse(row.CreatedAt, out created);
        return new NoteAudioRef(row.Id, row.DurationMs, row.ContentType, row.Size, created, cues);
    }
}
