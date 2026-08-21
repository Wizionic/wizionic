using App.Core.Storage;
using App.Core.Workflows;

namespace App.Maui.Services;

/// <summary>
/// Process-level due ticker (MAUI + Linux). Same 8s / 1min cadence as
/// <c>WorkflowDueBootstrap</c>, but not tied to the Blazor circuit.
/// WASM keeps the Razor loop.
/// </summary>
public sealed class WorkflowDueHost : IDisposable
{
    public const int StartupDelaySeconds = 8;
    public const int IntervalMinutes = 1;

    private readonly IWorkflowOrchestrator _orchestrator;
    private readonly IKeyStore _keys;
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public WorkflowDueHost(IWorkflowOrchestrator orchestrator, IKeyStore keys)
    {
        _orchestrator = orchestrator;
        _keys = keys;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _loop = RunLoopAsync(_cts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        try { cts?.Cancel(); }
        catch { /* ignore */ }

        if (loop is not null)
        {
            try { loop.Wait(TimeSpan.FromSeconds(5)); }
            catch { /* ignore cancel / timeout */ }
        }

        cts?.Dispose();
    }

    /// <summary>Immediate due pass (window restore / power resume). Shares the timer gate.</summary>
    public Task TickNowAsync(CancellationToken ct = default) => TickOnceAsync(ct);

    public void Dispose()
    {
        Stop();
        _tick.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkflowDue] tick failed: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(IntervalMinutes), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        await _tick.WaitAsync(ct);
        try
        {
            await _keys.LoadAsync(ct);
            await _orchestrator.ProjectCalendarsAsync(ct);
            await _orchestrator.ProcessDueAsync(ct);
        }
        finally
        {
            _tick.Release();
        }
    }
}
