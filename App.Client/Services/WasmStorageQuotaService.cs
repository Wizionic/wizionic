using App.Core.Auth;
using App.Core.Storage;
using Microsoft.JSInterop;
using System.Globalization;

namespace App.Client.Services;

public sealed class WasmStorageQuotaService : IStorageQuotaService
{
    private const string PercentKey = "app-storage-quota-percent";
    private const string HardCapKey = "app-storage-quota-hard-cap";

    private readonly IJSRuntime _js;
    private readonly IAuthService _auth;
    private readonly IGalleryStore _gallery;

    public WasmStorageQuotaService(
        IJSRuntime js,
        IAuthService auth,
        IGalleryStore gallery)
    {
        _js = js;
        _auth = auth;
        _gallery = gallery;
    }

    private string PrefKey(string baseKey) => StorageNamespace.GetPrefix(_auth) + baseKey;

    public async Task<StorageQuotaSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        double percent = 50;
        try
        {
            var p = await _js.InvokeAsync<string?>("idbGetSetting", PrefKey(PercentKey));
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                percent = Math.Clamp(v, 10, 90);
        }
        catch { /* default */ }

        long? hard = null;
        try
        {
            var h = await _js.InvokeAsync<string?>("idbGetSetting", PrefKey(HardCapKey));
            if (long.TryParse(h, out var hb) && hb > 0)
                hard = hb;
        }
        catch { /* default */ }

        return new StorageQuotaSettings(percent, hard);
    }

    public async Task SetSettingsAsync(StorageQuotaSettings settings, CancellationToken ct = default)
    {
        var percent = Math.Clamp(settings.PercentOfAvailable, 10, 90);
        await _js.InvokeVoidAsync("idbPutSetting", PrefKey(PercentKey), percent.ToString(CultureInfo.InvariantCulture));
        if (settings.HardCapBytes is > 0)
            await _js.InvokeVoidAsync("idbPutSetting", PrefKey(HardCapKey), settings.HardCapBytes.Value.ToString(CultureInfo.InvariantCulture));
        else
            await _js.InvokeVoidAsync("idbPutSetting", PrefKey(HardCapKey), "");
    }

    public async Task<long> MeasureAppUsageBytesAsync(CancellationToken ct = default)
    {
        var snap = await GetSnapshotAsync(ct);
        return snap.AppUsageBytes;
    }

    public async Task<StorageQuotaSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);

        // NEVER decrypt chat/notes/gallery bodies for quota — that freezes WASM.
        // Gallery: sum meta.size only. Browser: navigator.storage.estimate().
        long gallery = 0;
        try
        {
            gallery = await _gallery.SumStoredImageBytesAsync(ct);
        }
        catch { /* ignore */ }

        long browserUsage = 0, browserQuota = 0;
        try
        {
            var est = await _js.InvokeAsync<StorageEstimateDto>("storageEstimate");
            browserUsage = est?.usage ?? 0;
            browserQuota = est?.quota ?? 0;
        }
        catch { /* ignore */ }

        // Prefer browser-reported usage (includes encrypted IDB + overhead).
        // Gallery meta sum is a lower bound for the gallery category breakdown.
        var usage = Math.Max(browserUsage, gallery);
        var chat = 0L;
        var notes = 0L;
        var other = Math.Max(0, usage - gallery);

        long available = browserQuota;
        var source = browserQuota > 0 ? "browser-quota" : "unknown";
        if (available <= 0)
        {
            available = Math.Max(usage * 2, 512L * 1024 * 1024);
            source = "unknown";
        }

        var limitFromPercent = (long)(available * (settings.PercentOfAvailable / 100.0));
        var limit = settings.HardCapBytes is > 0
            ? Math.Min(limitFromPercent, settings.HardCapBytes.Value)
            : limitFromPercent;

        return new StorageQuotaSnapshot(
            usage, available, Math.Max(limit, 0), source,
            chat, notes, gallery, other);
    }

    public Task CompactAsync(CancellationToken ct = default) =>
        Task.CompletedTask;

    public async Task<bool> CanAcceptBytesAsync(long additionalBytes, CancellationToken ct = default)
    {
        if (additionalBytes <= 0) return true;
        var snap = await GetSnapshotAsync(ct);
        return snap.AppUsageBytes + additionalBytes <= snap.EffectiveLimitBytes;
    }

    private sealed class StorageEstimateDto
    {
        public long? usage { get; set; }
        public long? quota { get; set; }
    }
}
