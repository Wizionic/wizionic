namespace App.Core.Storage;

/// <summary>
/// Merges two versions of an album (list of images) instead of whole-album overwrite.
/// Images are matched by <see cref="GalleryImage.Id"/>; each id keeps the version with the
/// newer effective timestamp (ModifiedAt / DeletedAt / Timestamp). Local list order is preserved;
/// remote-only images are appended in remote order.
/// </summary>
public static class GallerySyncMerger
{
    public sealed record MergeResult(
        List<GalleryImage> Images,
        bool DiffersFromLocal,
        bool DiffersFromRemote,
        int RemoteOnlyCount,
        int LocalOnlyCount,
        int ResolvedConflicts);

    public static MergeResult Merge(
        IReadOnlyList<GalleryImage>? localImages,
        IReadOnlyList<GalleryImage>? remoteImages)
    {
        var local = NormalizeAll(localImages ?? Array.Empty<GalleryImage>());
        var remote = NormalizeAll(remoteImages ?? Array.Empty<GalleryImage>());

        if (local.Count == 0)
        {
            return new MergeResult(
                remote,
                DiffersFromLocal: remote.Count > 0,
                DiffersFromRemote: false,
                RemoteOnlyCount: remote.Count,
                LocalOnlyCount: 0,
                ResolvedConflicts: 0);
        }

        if (remote.Count == 0)
        {
            return new MergeResult(
                local,
                DiffersFromLocal: false,
                DiffersFromRemote: local.Count > 0,
                RemoteOnlyCount: 0,
                LocalOnlyCount: local.Count,
                ResolvedConflicts: 0);
        }

        var localById = IndexById(local);
        var remoteById = IndexById(remote);

        var chosen = new Dictionary<string, GalleryImage>(StringComparer.OrdinalIgnoreCase);
        var remoteOnly = 0;
        var localOnly = 0;
        var resolvedConflicts = 0;

        foreach (var id in localById.Keys.Union(remoteById.Keys, StringComparer.OrdinalIgnoreCase))
        {
            localById.TryGetValue(id, out var l);
            remoteById.TryGetValue(id, out var r);

            if (l is null)
            {
                chosen[id] = r!;
                remoteOnly++;
                continue;
            }

            if (r is null)
            {
                chosen[id] = l;
                localOnly++;
                continue;
            }

            if (ImageContentEquals(l, r))
            {
                chosen[id] = EffectiveTicks(r) > EffectiveTicks(l) ? r : l;
                continue;
            }

            resolvedConflicts++;
            chosen[id] = PickNewer(l, r);
        }

        var result = new List<GalleryImage>(chosen.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var img in local)
        {
            var id = img.Id;
            if (!chosen.TryGetValue(id, out var picked))
                continue;
            if (!seen.Add(id))
                continue;
            result.Add(picked);
        }

        foreach (var img in remote)
        {
            var id = img.Id;
            if (!seen.Add(id))
                continue;
            if (!chosen.TryGetValue(id, out var picked))
                continue;
            result.Add(picked);
        }

        var differsFromLocal = !ImageListsEquivalent(local, result);
        var differsFromRemote = !ImageListsEquivalent(remote, result);

        return new MergeResult(
            result,
            differsFromLocal,
            differsFromRemote,
            remoteOnly,
            localOnly,
            resolvedConflicts);
    }

    public static List<GalleryImage> MergeImages(
        IReadOnlyList<GalleryImage>? localImages,
        IReadOnlyList<GalleryImage>? remoteImages) =>
        Merge(localImages, remoteImages).Images;

    public static long GetLatestContentTicks(IEnumerable<GalleryImage> images)
    {
        long max = 0;
        foreach (var img in images)
        {
            max = Math.Max(max, EffectiveTicks(img));
        }
        return max;
    }

    public static List<GalleryImage> NormalizeAll(IEnumerable<GalleryImage> images)
    {
        var list = new List<GalleryImage>();
        foreach (var img in images)
        {
            var id = string.IsNullOrWhiteSpace(img.Id) ? Guid.NewGuid().ToString("N") : img.Id;
            list.Add(img with { Id = id });
        }
        return list;
    }

    private static Dictionary<string, GalleryImage> IndexById(List<GalleryImage> images)
    {
        var map = new Dictionary<string, GalleryImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var img in images)
        {
            if (string.IsNullOrWhiteSpace(img.Id))
                continue;
            map[img.Id] = img;
        }
        return map;
    }

    private static GalleryImage PickNewer(GalleryImage a, GalleryImage b)
    {
        var ta = EffectiveTicks(a);
        var tb = EffectiveTicks(b);
        if (tb > ta)
            return b;
        if (ta > tb)
            return a;

        if (a.DeletedAt.HasValue != b.DeletedAt.HasValue)
            return a.DeletedAt.HasValue ? a : b;

        var cmp = string.CompareOrdinal(a.DataBase64 ?? "", b.DataBase64 ?? "");
        return cmp >= 0 ? a : b;
    }

    private static long EffectiveTicks(GalleryImage img)
    {
        long max = 0;
        if (img.ModifiedAt.HasValue)
            max = Math.Max(max, img.ModifiedAt.Value.Ticks);
        if (img.DeletedAt.HasValue)
            max = Math.Max(max, img.DeletedAt.Value.Ticks);
        if (img.Timestamp.HasValue)
            max = Math.Max(max, img.Timestamp.Value.Ticks);
        return max;
    }

    private static bool ImageContentEquals(GalleryImage a, GalleryImage b)
    {
        if (a.DeletedAt.HasValue != b.DeletedAt.HasValue)
            return false;
        if (!string.Equals(a.DataBase64 ?? "", b.DataBase64 ?? "", StringComparison.Ordinal))
            return false;
        if (!string.Equals(a.ContentType ?? "", b.ContentType ?? "", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool ImageListsEquivalent(List<GalleryImage> a, List<GalleryImage> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Id, b[i].Id, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!ImageContentEquals(a[i], b[i]))
                return false;
        }

        return true;
    }
}
