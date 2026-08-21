namespace App.Maui;

/// <summary>
/// Per-session single-instance gate for unpackaged Windows MAUI.
/// Mutex after Velopack.Run(); wait loop starts only after <see cref="WindowsDesktopHost.Attach"/>.
/// </summary>
internal static class WindowsSingleInstance
{
    public const string MutexName = @"Local\Wizionic.Desktop.SingleInstance";
    public const string ActivateEventName = @"Local\Wizionic.Desktop.Activate";
    public const string QuitEventName = @"Local\Wizionic.Desktop.Quit";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static EventWaitHandle? _quit;
    private static CancellationTokenSource? _cts;
    private static Thread? _waitThread;

    /// <summary>
    /// Try to become the primary process. <see cref="AbandonedMutexException"/> counts as acquired
    /// (Task Manager kill / Velopack replacing <c>current\</c> leaves the mutex abandoned).
    /// </summary>
    public static bool TryAcquirePrimary()
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);

        bool createdNew;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            Console.WriteLine("[Desktop] acquired abandoned single-instance mutex");
            return true;
        }

        if (createdNew)
            return true;

        try
        {
            if (_mutex.WaitOne(TimeSpan.Zero))
                return true;
        }
        catch (AbandonedMutexException)
        {
            Console.WriteLine("[Desktop] acquired abandoned single-instance mutex");
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public static void RequestShow()
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            ev.Set();
            Console.WriteLine("[Desktop] signaled existing instance to show");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] RequestShow failed: {ex.Message}");
        }
    }

    public static void RequestQuit()
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            ev.Set();
            Console.WriteLine("[Desktop] signaled existing instance to quit");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] RequestQuit signal failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Call only after Attach so Show() has a DispatcherQueue / AppWindow.
    /// Auto-reset events stay signaled until waited, so a second launch during startup is not lost.
    /// </summary>
    public static void StartWaitLoop(Action onActivate, Action onQuit)
    {
        StopWaitLoop();

        _activate ??= new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _quit ??= new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var activate = _activate;
        var quit = _quit;

        _waitThread = new Thread(() =>
        {
            WaitHandle[] handles = [activate, quit, token.WaitHandle];
            while (!token.IsCancellationRequested)
            {
                int idx;
                try
                {
                    idx = WaitHandle.WaitAny(handles);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (AbandonedMutexException)
                {
                    continue;
                }

                if (token.IsCancellationRequested || idx == 2)
                    break;

                try
                {
                    if (idx == 0)
                        onActivate();
                    else if (idx == 1)
                    {
                        onQuit();
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Desktop] single-instance wait: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "Wizionic.SingleInstance"
        };
        _waitThread.Start();
        Console.WriteLine("[Desktop] single-instance wait loop started");
    }

    public static void StopWaitLoop()
    {
        try { _cts?.Cancel(); }
        catch { /* ignore */ }

        var thread = _waitThread;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try { thread.Join(TimeSpan.FromSeconds(1)); }
            catch { /* ignore */ }
        }

        _waitThread = null;
        try { _cts?.Dispose(); }
        catch { /* ignore */ }
        _cts = null;
    }
}
