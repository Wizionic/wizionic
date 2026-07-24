using System.Diagnostics;
using System.Collections.ObjectModel;
using ChatfishApp.Core.Browser;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Cross-platform download list + open/reveal/delete helpers for the embedded browser.
/// Platform WebView hooks call Begin/Update/Complete; the Blazor toolbar binds to this service.
/// </summary>
public sealed class BrowserDownloadService : IBrowserDownloadService
{
    private readonly object _gate = new();
    private readonly List<BrowserDownloadItem> _items = [];
    private readonly int _maxItems = 40;

    public IReadOnlyList<BrowserDownloadItem> Downloads
    {
        get
        {
            lock (_gate)
                return new ReadOnlyCollection<BrowserDownloadItem>(_items.ToList());
        }
    }

    public bool IsAnyInProgress
    {
        get
        {
            lock (_gate)
                return _items.Any(i => i.State == BrowserDownloadState.InProgress);
        }
    }

    public event Action? Changed;

    public BrowserDownloadItem Begin(string url, string filePath, string? fileName = null)
    {
        var item = new BrowserDownloadItem
        {
            Url = url ?? "",
            FilePath = filePath,
            FileName = string.IsNullOrWhiteSpace(fileName)
                ? (Path.GetFileName(filePath) is { Length: > 0 } n ? n : "download")
                : fileName!,
            State = BrowserDownloadState.InProgress,
            StartedAtUtc = DateTime.UtcNow
        };

        lock (_gate)
        {
            _items.Insert(0, item);
            while (_items.Count > _maxItems)
                _items.RemoveAt(_items.Count - 1);
        }

        RaiseChanged();
        return item;
    }

    public void Update(string id, Action<BrowserDownloadItem> mutator)
    {
        if (string.IsNullOrWhiteSpace(id) || mutator == null)
            return;

        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null)
                return;
            mutator(item);
        }

        RaiseChanged();
    }

    public void Complete(string id, string? finalPath = null)
    {
        Update(id, item =>
        {
            if (!string.IsNullOrWhiteSpace(finalPath))
            {
                item.FilePath = finalPath;
                item.FileName = Path.GetFileName(finalPath) is { Length: > 0 } n ? n : item.FileName;
            }
            item.State = BrowserDownloadState.Completed;
            item.CompletedAtUtc = DateTime.UtcNow;
            item.ErrorMessage = null;
        });
    }

    public void Fail(string id, string? message = null)
    {
        Update(id, item =>
        {
            item.State = BrowserDownloadState.Failed;
            item.CompletedAtUtc = DateTime.UtcNow;
            item.ErrorMessage = message;
        });
    }

    public void Cancel(string id)
    {
        Update(id, item =>
        {
            item.State = BrowserDownloadState.Cancelled;
            item.CompletedAtUtc = DateTime.UtcNow;
        });
    }

    public Task OpenAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetPath(id);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Task.CompletedTask;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] open download failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task ShowInFolderAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetPath(id);
        if (string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = dir,
                            UseShellExecute = true
                        });
                    }
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                // Prefer select-in-folder when the file exists (Nautilus/Dolphin via xdg-open parent).
                var target = File.Exists(path)
                    ? Path.GetDirectoryName(path)
                    : (Directory.Exists(path) ? path : Path.GetDirectoryName(path));
                if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
                    return Task.CompletedTask;

                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = target,
                    UseShellExecute = false
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] show-in-folder failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string? path;
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            path = item?.FilePath;
            _items.RemoveAll(i => i.Id == id);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Browser] delete download failed: {ex.Message}");
            }
        }

        RaiseChanged();
        return Task.CompletedTask;
    }

    public void RemoveFromList(string id)
    {
        lock (_gate)
            _items.RemoveAll(i => i.Id == id);
        RaiseChanged();
    }

    public void ClearCompleted()
    {
        lock (_gate)
        {
            _items.RemoveAll(i =>
                i.State is BrowserDownloadState.Completed
                    or BrowserDownloadState.Failed
                    or BrowserDownloadState.Cancelled);
        }
        RaiseChanged();
    }

    private string? GetPath(string id)
    {
        lock (_gate)
            return _items.FirstOrDefault(i => i.Id == id)?.FilePath;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[Browser] download Changed handler: {ex.Message}"); }
    }

    /// <summary>Default Downloads folder for the current OS user.</summary>
    public static string GetDefaultDownloadsFolder()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var downloads = Path.Combine(userProfile, "Downloads");
                if (Directory.Exists(downloads))
                    return downloads;
            }
            else if (OperatingSystem.IsLinux())
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR");
                if (!string.IsNullOrWhiteSpace(xdg) && Directory.Exists(xdg))
                    return xdg;
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var downloads = Path.Combine(home, "Downloads");
                if (Directory.Exists(downloads))
                    return downloads;
                return home;
            }
        }
        catch { /* fall through */ }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{name}-{Guid.NewGuid():N}{ext}");
    }
}
