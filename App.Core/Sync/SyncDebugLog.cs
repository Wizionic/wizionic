using System.Collections.Concurrent;
using System.Text;

namespace App.Core.Sync;

/// <summary>
/// In-memory ring buffer (+ optional file) for WebRTC/sync diagnostics.
/// Always mirrors to Console; when <see cref="Enabled"/>, also keeps lines for the Sync page UI and optional file.
/// </summary>
public static class SyncDebugLog
{
    private const int MaxLines = 400;
    private static readonly ConcurrentQueue<string> Lines = new();
    private static readonly object FileLock = new();
    private static string? _logFilePath;
    private static bool _enabled;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Write("system", value ? "Sync debug logging ENABLED" : "Sync debug logging disabled (console still active)");
            Changed?.Invoke();
        }
    }

    /// <summary>Optional path for append-only log file (set by MAUI at startup).</summary>
    public static string? LogFilePath
    {
        get => _logFilePath;
        set
        {
            _logFilePath = value;
            if (!string.IsNullOrWhiteSpace(value))
                Write("system", $"Sync log file: {value}");
        }
    }

    public static event Action? Changed;

    public static IReadOnlyList<string> Snapshot() => Lines.ToArray();

    public static string SnapshotText()
    {
        var sb = new StringBuilder();
        foreach (var line in Lines)
            sb.AppendLine(line);
        return sb.ToString();
    }

    public static void Clear()
    {
        while (Lines.TryDequeue(out _)) { }
        Changed?.Invoke();
    }

    public static void Write(string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}";
        // Always print — Linux AppImage: launch from terminal to see this.
        Console.WriteLine(line);

        Lines.Enqueue(line);
        while (Lines.Count > MaxLines && Lines.TryDequeue(out _)) { }

        if (_enabled && !string.IsNullOrWhiteSpace(_logFilePath))
        {
            try
            {
                lock (FileLock)
                {
                    var dir = Path.GetDirectoryName(_logFilePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Ignore disk errors; in-memory buffer still works.
            }
        }

        try { Changed?.Invoke(); } catch { /* UI subscribers */ }
    }

    public static void Info(string message) => Write("sync", message);
    public static void Warn(string message) => Write("warn", message);
    public static void Error(string message) => Write("error", message);
    public static void Browser(string message) => Write("browser", message);
    public static void WebRtc(string message) => Write("webrtc", message);
    public static void Hub(string message) => Write("hub", message);
}
