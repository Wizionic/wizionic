namespace App.Core.Storage;

public sealed class NullStorageQuotaService : IStorageQuotaService
{
    public static NullStorageQuotaService Instance { get; } = new();

    public Task<StorageQuotaSettings> GetSettingsAsync(CancellationToken ct = default) =>
        Task.FromResult(new StorageQuotaSettings());

    public Task SetSettingsAsync(StorageQuotaSettings settings, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<StorageQuotaSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        Task.FromResult(new StorageQuotaSnapshot(0, 0, 0, "unknown"));

    public Task<bool> CanAcceptBytesAsync(long additionalBytes, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<long> MeasureAppUsageBytesAsync(CancellationToken ct = default) =>
        Task.FromResult(0L);

    public Task CompactAsync(CancellationToken ct = default) => Task.CompletedTask;
}
