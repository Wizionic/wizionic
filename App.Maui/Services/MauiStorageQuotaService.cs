using App.Core.Auth;
using App.Core.Storage;
using System.Globalization;

namespace App.Maui.Services;

public sealed class MauiStorageQuotaService : IStorageQuotaService
{
    private const string PercentKey = "app-storage-quota-percent";
    private const string HardCapKey = "app-storage-quota-hard-cap";

    private readonly SqliteSettingsDatabase _settings;
    private readonly SqliteHistoryDatabase _history;
    private readonly IAuthService _auth;
    private readonly string _dataRoot;

    public MauiStorageQuotaService(
        SqliteSettingsDatabase settings,
        SqliteHistoryDatabase history,
        IAuthService auth)
    {
        _settings = settings;
        _history = history;
        _auth = auth;
        _dataRoot = MauiAppData.Directory;
    }

    private string PrefKey(string baseKey)
    {
        var ns = StorageNamespace.GetPrefix(_auth);
        return ns + baseKey;
    }

    public async Task<StorageQuotaSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        double percent = 50;
        var p = await _settings.GetStringAsync(PrefKey(PercentKey), ct);
        if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            percent = Math.Clamp(v, 10, 90);

        long? hard = null;
        var h = await _settings.GetStringAsync(PrefKey(HardCapKey), ct);
        if (long.TryParse(h, out var hb) && hb > 0)
            hard = hb;

        return new StorageQuotaSettings(percent, hard);
    }

    public async Task SetSettingsAsync(StorageQuotaSettings settings, CancellationToken ct = default)
    {
        var percent = Math.Clamp(settings.PercentOfAvailable, 10, 90);
        await _settings.SetStringAsync(PrefKey(PercentKey), percent.ToString(CultureInfo.InvariantCulture), ct);
        await _settings.SetStringAsync(
            PrefKey(HardCapKey),
            settings.HardCapBytes is > 0 ? settings.HardCapBytes.Value.ToString(CultureInfo.InvariantCulture) : "",
            ct);
    }

    public async Task<long> MeasureAppUsageBytesAsync(CancellationToken ct = default)
    {
        var snap = await GetSnapshotAsync(ct);
        return snap.AppUsageBytes;
    }

    public async Task<StorageQuotaSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);

        // Live content lengths (encrypted payloads) — reflects deletes without waiting for VACUUM.
        var chat = await _history.SumContentLengthAsync("conversation_content", ct);
        var notes = await _history.SumContentLengthAsync("note_content", ct);
        var galleryImages = await _history.SumContentLengthAsync("album_image_content", ct);
        var galleryLegacy = await _history.SumContentLengthAsync("album_content", ct);
        var gallery = galleryImages + galleryLegacy;
        var noteAudio = await _history.SumContentLengthAsync("note_audio_content", ct);

        // On-disk size of the app data DB only (not help_rag.db / other files under AppData).
        long dbBytes = 0;
        try
        {
            dbBytes = FileSizeIfExists(_history.DatabasePath)
                      + FileSizeIfExists(_history.DatabasePath + "-wal")
                      + FileSizeIfExists(_history.DatabasePath + "-shm");
        }
        catch { /* ignore */ }

        // "Used" on the Sync page is live encrypted content so deletes drop immediately.
        // OtherBytes is SQLite file overhead (WAL / freelist) until Compact.
        var contentSum = chat + notes + gallery + noteAudio;
        var usage = contentSum;
        var other = Math.Max(0, dbBytes - contentSum);

        long free = 0;
        var source = "unknown";
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_dataRoot));
            if (!string.IsNullOrEmpty(root))
            {
                free = new DriveInfo(root).AvailableFreeSpace;
                source = "disk-free";
            }
        }
        catch { /* ignore */ }

        if (free <= 0)
        {
            free = Math.Max(usage * 2, 2L * 1024 * 1024 * 1024);
            source = "unknown";
        }

        var limitFromPercent = (long)(free * (settings.PercentOfAvailable / 100.0));
        var limit = settings.HardCapBytes is > 0
            ? Math.Min(limitFromPercent, settings.HardCapBytes.Value)
            : limitFromPercent;

        return new StorageQuotaSnapshot(
            usage, free, Math.Max(limit, 0), source,
            chat, notes, gallery, other, noteAudio);
    }

    public async Task CompactAsync(CancellationToken ct = default)
    {
        // Drop leftover whole-album blobs from the first gallery implementation.
        await _history.PurgeLegacyAlbumContentAsync(ct);
        await _history.VacuumAsync(ct);
    }

    public async Task<bool> CanAcceptBytesAsync(long additionalBytes, CancellationToken ct = default)
    {
        if (additionalBytes <= 0) return true;
        var snap = await GetSnapshotAsync(ct);
        return snap.AppUsageBytes + additionalBytes <= snap.EffectiveLimitBytes;
    }

    private static long FileSizeIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
