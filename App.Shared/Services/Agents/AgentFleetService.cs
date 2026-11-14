using System.Text.Json;
using App.Core.Agents;
using App.Core.Auth;
using App.Core.Skills;
using App.Core.Storage;
using App.Core.Sync;

namespace App.Shared.Services.Agents;

/// <summary>
/// Orchestrator and worker for skill runs between signed-in devices.
/// The worker owns the run log. The orchestrator keeps a read-only receipt.
/// Frames ride SignalR (<see cref="A2AChannel.MessageType"/>) and the body is encrypted
/// with the account key before it reaches the hub.
///
/// Step and status frames are best-effort. This service does not wait for a per-event ack:
/// a round trip on every tool call can stall the skill. The SignalR send is the delivery
/// boundary. If the orchestrator hears nothing, it marks the receipt failed or behind and
/// leaves the worker log in place.
/// </summary>
public sealed class AgentFleetService : IDisposable
{
    public const string OrchestratorPrefKey = "app-fleet-orchestrator";
    public const string ReceiptsPrefKey = "app-agent-receipts-v1";
    private const int MaxReceipts = 40;
    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WorkingSilence = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _workerRuns = new(StringComparer.Ordinal);
    private readonly List<AgentTaskReceipt> _receipts = new();

    private ISyncService? _sync;
    private ISkillRunner? _runner;
    private ICryptoService? _crypto;
    private IAuthService? _auth;
    private ISyncPreferencesStore? _prefs;
    private ISkillStore? _skills;
    private ISkillRunLogStore? _logs;
    private Action? _onSyncChanged;
    private Action<string, string>? _onFrame;
    private Action? _onSkillsChanged;
    private Timer? _watch;
    private int _seq;
    private bool _ready;
    private bool _userToggled;
    private bool _wantOrchestrator;
    private bool _leaseHeld;
    private bool _wasConnected;
    private DateTimeOffset _lastPublishUtc;
    private int _publishQueued;

    public event Action? Changed;

    public bool IsAttached { get; private set; }
    public bool WantOrchestrator => _wantOrchestrator;
    public string? StatusMessage { get; private set; }
    public string? LastError { get; private set; }

    public bool IsConnected => _sync?.IsConnected == true;

    /// <summary>True when this device holds the lease, including the moment the hub just accepted the claim.</summary>
    public bool IsOrchestrator => _leaseHeld || ServerSaysOrchestrator();

    public IReadOnlyList<AgentTaskReceipt> Receipts
    {
        get
        {
            lock (_gate)
                return _receipts.ToList();
        }
    }

    /// <summary>
    /// Called from the layout once the client runtime is up.
    /// Skips the host's <see cref="NullSyncService"/> so a server circuit does not capture scoped services.
    /// The sync, runner, and crypto instances are the circuit's (WASM) or the app's (MAUI).
    /// </summary>
    public void Attach(
        ISyncService sync,
        ISkillRunner? runner,
        ICryptoService? crypto,
        IAuthService? auth,
        ISyncPreferencesStore? prefs,
        ISkillStore? skills,
        ISkillRunLogStore? logs)
    {
        if (sync is NullSyncService)
            return;
        if (IsAttached && ReferenceEquals(_sync, sync))
        {
            // MAUI keeps sync as a singleton and crypto as scoped. A new circuit should
            // refresh the scoped services without subscribing twice.
            _runner = runner ?? _runner;
            _crypto = crypto ?? _crypto;
            _auth = auth ?? _auth;
            _prefs = prefs ?? _prefs;
            _skills = skills ?? _skills;
            _logs = logs ?? _logs;
            return;
        }

        DetachHandlers();
        _sync = sync;
        _runner = runner;
        _crypto = crypto;
        _auth = auth;
        _prefs = prefs;
        _skills = skills;
        _logs = logs;
        IsAttached = true;

        _onSyncChanged = OnSyncChanged;
        _onFrame = OnFrame;
        _onSkillsChanged = OnSkillsChanged;
        _sync.OnChanged += _onSyncChanged;
        _sync.OnA2AFrameReceived += _onFrame;
        if (_skills is not null && _onSkillsChanged is not null)
            _skills.Changed += _onSkillsChanged;

        _watch = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _ = InitializeAsync();
    }

    public IReadOnlyList<SyncDeviceInfo> OnlinePeers()
    {
        if (_sync is null)
            return Array.Empty<SyncDeviceInfo>();
        return _sync.Devices
            .Where(d => d.IsOnline && !_sync.IsSelf(d.DeviceId))
            .ToList();
    }

    public async Task SetOrchestratorAsync(bool want)
    {
        _userToggled = true;
        _wantOrchestrator = want;
        if (!want)
            _leaseHeld = false;
        if (_prefs is not null)
        {
            try { await _prefs.SetStringAsync(OrchestratorPrefKey, want ? "1" : "0"); }
            catch { /* shown on the next publish */ }
        }
        await PublishAsync();
        Changed?.Invoke();
    }

    public async Task<string?> StartSkillAsync(string deviceId, string skillId, string? userMessage)
    {
        LastError = null;
        if (!IsOrchestrator)
        {
            LastError = "Turn this device on as the orchestrator first.";
            Changed?.Invoke();
            return null;
        }
        if (_sync is null || string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(skillId))
        {
            LastError = "Pick a device and a skill.";
            Changed?.Invoke();
            return null;
        }
        if (_sync.IsSelf(deviceId))
        {
            LastError = "Pick another device.";
            Changed?.Invoke();
            return null;
        }
        if (string.IsNullOrEmpty(_auth?.LocalEncryptionKeyB64) || _crypto is null)
        {
            LastError = "Sign in so this device can encrypt agent results.";
            Changed?.Invoke();
            return null;
        }

        var peer = _sync.Devices.FirstOrDefault(d =>
            string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        var skill = peer?.Skills?.FirstOrDefault(s =>
            string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase));
        var taskId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var receipt = new AgentTaskReceipt
        {
            TaskId = taskId,
            WorkerDeviceId = deviceId,
            WorkerName = string.IsNullOrWhiteSpace(peer?.Name) ? deviceId : peer!.Name,
            SkillId = skillId,
            SkillName = string.IsNullOrWhiteSpace(skill?.Name) ? skillId : skill!.Name,
            UserMessage = string.IsNullOrWhiteSpace(userMessage) ? null : userMessage.Trim(),
            StartedAtUtc = now,
            LastEventUtc = now,
            State = A2ATaskState.Submitted
        };

        lock (_gate)
        {
            _receipts.Insert(0, receipt);
            while (_receipts.Count > MaxReceipts)
                _receipts.RemoveAt(_receipts.Count - 1);
        }

        var sent = await SendFrameAsync(deviceId, taskId, A2AMethod.Send, new A2ASendBody
        {
            SkillId = skillId,
            SkillName = receipt.SkillName,
            UserMessage = receipt.UserMessage
        });
        if (!sent)
        {
            receipt.State = A2ATaskState.Failed;
            receipt.Behind = true;
            receipt.EndedAtUtc = DateTimeOffset.UtcNow;
            receipt.Error = LastError ?? "Could not reach that device.";
            if (string.IsNullOrEmpty(LastError))
                LastError = receipt.Error;
        }
        else
        {
            receipt.LastEventUtc = DateTimeOffset.UtcNow;
        }

        await SaveReceiptsAsync();
        Changed?.Invoke();
        return taskId;
    }

    public async Task CancelAsync(string taskId)
    {
        AgentTaskReceipt? receipt;
        lock (_gate)
            receipt = _receipts.FirstOrDefault(r => r.TaskId == taskId);
        if (receipt is null || IsTerminal(receipt.State))
            return;
        await SendFrameAsync(receipt.WorkerDeviceId, taskId, A2AMethod.Cancel, new A2AStatusBody());
    }

    public async Task SendFeedbackAsync(string taskId, string stepId, string verdict, string? note)
    {
        LastError = null;
        AgentTaskReceipt? receipt;
        lock (_gate)
            receipt = _receipts.FirstOrDefault(r => r.TaskId == taskId);
        if (receipt is null || string.IsNullOrEmpty(receipt.WorkerLogId))
        {
            LastError = "The worker has not stored that step yet.";
            Changed?.Invoke();
            return;
        }
        var sent = await SendFrameAsync(receipt.WorkerDeviceId, taskId, A2AMethod.Feedback, new A2AFeedbackBody
        {
            LogId = receipt.WorkerLogId,
            StepId = stepId,
            Verdict = verdict,
            Note = note
        });
        if (!sent && string.IsNullOrEmpty(LastError))
            LastError = "Could not send that note to the worker.";
        Changed?.Invoke();
    }

    public async Task DeleteReceiptAsync(string taskId)
    {
        lock (_gate)
            _receipts.RemoveAll(r => r.TaskId == taskId);
        await SaveReceiptsAsync();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        DetachHandlers();
        List<CancellationTokenSource> runs;
        lock (_gate)
            runs = _workerRuns.Values.ToList();
        foreach (var cts in runs)
        {
            try { cts.Cancel(); }
            catch { /* already canceled */ }
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (_prefs is not null)
            {
                if (!_userToggled)
                {
                    var flag = await _prefs.GetStringAsync(OrchestratorPrefKey);
                    _wantOrchestrator = flag == "1";
                }
                var json = await _prefs.GetStringAsync(ReceiptsPrefKey);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        var list = JsonSerializer.Deserialize<List<AgentTaskReceipt>>(json, A2AJson.Options);
                        if (list is not null)
                        {
                            lock (_gate)
                            {
                                _receipts.Clear();
                                _receipts.AddRange(list.Take(MaxReceipts));
                            }
                        }
                    }
                    catch { /* corrupt receipts are dropped */ }
                }
            }
            if (_skills is not null)
                await _skills.LoadAsync();
            if (_logs is not null)
                await _logs.LoadAsync();
        }
        catch
        {
            /* publish still tries */
        }

        _ready = true;
        _wasConnected = _sync?.IsConnected == true;
        await PublishAsync();
        Changed?.Invoke();
    }

    private void OnSkillsChanged()
    {
        if (_ready)
            _ = PublishAsync();
    }

    private void OnSyncChanged()
    {
        var connected = _sync?.IsConnected == true;
        var rose = connected && !_wasConnected;
        _wasConnected = connected;
        if (ServerSaysOrchestrator())
            _leaseHeld = true;
        else if (!_wantOrchestrator)
            _leaseHeld = false;
        Changed?.Invoke();
        if (!_ready || !connected)
            return;
        var due = rose || CardMissing() || WantLeaseButFree();
        if (due && DateTimeOffset.UtcNow - _lastPublishUtc > TimeSpan.FromSeconds(2))
            _ = PublishAsync();
    }

    private void OnFrame(string fromDeviceId, string payloadJson) =>
        _ = HandleFrameAsync(fromDeviceId, payloadJson);

    private async Task PublishAsync()
    {
        if (Interlocked.Exchange(ref _publishQueued, 1) == 1)
            return;
        try
        {
            do
            {
                Interlocked.Exchange(ref _publishQueued, 0);
                await _publishGate.WaitAsync();
                try
                {
                    if (_ready)
                        await PublishCoreAsync();
                }
                finally { _publishGate.Release(); }
            }
            while (Volatile.Read(ref _publishQueued) == 1);
        }
        catch
        {
            Interlocked.Exchange(ref _publishQueued, 0);
        }
    }

    private async Task PublishCoreAsync()
    {
        if (_sync is null || !_sync.IsConnected)
            return;
        _lastPublishUtc = DateTimeOffset.UtcNow;
        AgentCardPublishResult result;
        try
        {
            result = await _sync.PublishAgentCardAsync(BuildCards(), _wantOrchestrator, releaseOrchestrator: !_wantOrchestrator);
        }
        catch
        {
            StatusMessage = "Could not publish this device.";
            Changed?.Invoke();
            return;
        }

        if (result.Denied)
        {
            _wantOrchestrator = false;
            _leaseHeld = false;
            StatusMessage = "Another device is the orchestrator.";
            if (_prefs is not null)
            {
                try { await _prefs.SetStringAsync(OrchestratorPrefKey, "0"); }
                catch { /* the message is enough */ }
            }
        }
        else if (!result.Ok)
        {
            StatusMessage = "Could not publish this device.";
            if (!_wantOrchestrator)
                _leaseHeld = false;
        }
        else
        {
            _leaseHeld = _wantOrchestrator;
            StatusMessage = null;
        }
        Changed?.Invoke();
    }

    private List<AgentSkillCard> BuildCards()
    {
        var list = new List<AgentSkillCard>();
        if (_skills is null)
            return list;
        foreach (var record in _skills.List())
        {
            if (list.Count >= 40 || !record.Enabled || string.IsNullOrWhiteSpace(record.Id))
                continue;
            var name = record.Name ?? "";
            var description = "";
            try
            {
                var doc = SkillMarkdown.Parse(record.Markdown ?? "");
                if (!string.IsNullOrWhiteSpace(doc.Name))
                    name = doc.Name;
                description = doc.Description ?? "";
            }
            catch
            {
                /* publish the stored name anyway */
            }
            list.Add(new AgentSkillCard
            {
                Id = Trim(record.Id, 64),
                Name = Trim(string.IsNullOrWhiteSpace(name) ? record.Id : name, 80),
                Description = Trim(OneLine(description), 180)
            });
        }
        return list;
    }

    private bool CardMissing()
    {
        if (_sync is null || _skills is null)
            return false;
        if (BuildCards().Count == 0)
            return false;
        var me = _sync.Devices.FirstOrDefault(d => _sync.IsSelf(d.DeviceId));
        return me is not null && (me.Skills is null || me.Skills.Count == 0);
    }

    private bool WantLeaseButFree()
    {
        if (!_wantOrchestrator || ServerSaysOrchestrator() || _sync is null)
            return false;
        return !_sync.Devices.Any(d => d.IsOnline && d.IsOrchestrator && !_sync.IsSelf(d.DeviceId));
    }

    private bool ServerSaysOrchestrator()
    {
        if (_sync is null)
            return false;
        var me = _sync.Devices.FirstOrDefault(d => _sync.IsSelf(d.DeviceId));
        return me?.IsOrchestrator == true;
    }

    private async Task HandleFrameAsync(string fromDeviceId, string payloadJson)
    {
        try
        {
            if (_sync is not null && _sync.IsSelf(fromDeviceId))
                return;
            var envelope = JsonSerializer.Deserialize<A2AEnvelope>(payloadJson, A2AJson.Options);
            if (envelope is null || string.IsNullOrWhiteSpace(envelope.TaskId) || string.IsNullOrWhiteSpace(envelope.Cipher))
                return;
            var json = await DecryptAsync(envelope.Cipher);
            if (json is null)
                return;

            switch (envelope.Method)
            {
                case A2AMethod.Send:
                    StartWorker(fromDeviceId, envelope.TaskId, json);
                    break;
                case A2AMethod.Cancel:
                    CancelWorker(envelope.TaskId);
                    break;
                case A2AMethod.Feedback:
                    await ApplyFeedbackAsync(fromDeviceId, envelope.TaskId, json);
                    break;
                case A2AMethod.Step:
                case A2AMethod.Status:
                case A2AMethod.FeedbackSaved:
                    if (ApplyOrchestratorFrame(envelope.Method, envelope.TaskId, json))
                    {
                        await SaveReceiptsAsync();
                        Changed?.Invoke();
                    }
                    break;
            }
        }
        catch
        {
            /* a bad frame does not take down the hub handler */
        }
    }

    private void StartWorker(string orchestratorId, string taskId, string json)
    {
        var body = JsonSerializer.Deserialize<A2ASendBody>(json, A2AJson.Options);
        if (body is null || string.IsNullOrWhiteSpace(body.SkillId) || _runner is null)
            return;
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_workerRuns.ContainsKey(taskId))
            {
                cts.Dispose();
                return;
            }
            _workerRuns[taskId] = cts;
        }
        _ = RunWorkerAsync(orchestratorId, taskId, body, cts);
    }

    private async Task RunWorkerAsync(string orchestratorId, string taskId, A2ASendBody body, CancellationTokenSource cts)
    {
        var sentSteps = new HashSet<string>(StringComparer.Ordinal);
        string? logId = null;
        var beat = HeartbeatAsync(orchestratorId, taskId, cts.Token);
        try
        {
            await SendStatusAsync(orchestratorId, taskId, A2ATaskState.Submitted, null, null, null);
            var result = await _runner!.RunAsync(new SkillRunRequest
            {
                SkillId = body.SkillId,
                UserMessageOverride = body.UserMessage,
                Source = SkillRunSource.A2A,
                TaskId = taskId,
                TriggerDetail = "a2a",
                OnRunStarted = id =>
                {
                    logId = id;
                    _ = SendStatusAsync(orchestratorId, taskId, A2ATaskState.Working, null, null, id);
                },
                OnSteps = steps =>
                {
                    foreach (var step in steps)
                    {
                        if (string.IsNullOrEmpty(step.StepId))
                            continue;
                        lock (sentSteps)
                        {
                            if (!sentSteps.Add(step.StepId))
                                continue;
                        }
                        _ = SendStepAsync(orchestratorId, taskId, step);
                    }
                }
            }, cts.Token);

            var state = result.Log?.State == SkillRunState.Canceled || cts.IsCancellationRequested
                ? A2ATaskState.Canceled
                : result.Success ? A2ATaskState.Completed : A2ATaskState.Failed;
            await SendStatusAsync(
                orchestratorId,
                taskId,
                state,
                Trim(result.Text, 8000),
                result.Error,
                result.Log?.Id ?? logId);
        }
        catch (Exception ex)
        {
            var state = ex is OperationCanceledException || cts.IsCancellationRequested
                ? A2ATaskState.Canceled
                : A2ATaskState.Failed;
            await SendStatusAsync(orchestratorId, taskId, state, null, ex.Message, logId);
        }
        finally
        {
            try { cts.Cancel(); } catch { /* stopping the heartbeat */ }
            try { await beat; } catch { /* heartbeat ended */ }
            lock (_gate)
                _workerRuns.Remove(taskId);
            cts.Dispose();
        }
    }

    private async Task HeartbeatAsync(string orchestratorId, string taskId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatEvery, ct);
                await SendStatusAsync(orchestratorId, taskId, A2ATaskState.Working, null, null, null);
            }
        }
        catch (OperationCanceledException)
        {
            /* run finished */
        }
    }

    private void CancelWorker(string taskId)
    {
        CancellationTokenSource? cts;
        lock (_gate)
            _workerRuns.TryGetValue(taskId, out cts);
        try { cts?.Cancel(); }
        catch { /* already canceled */ }
    }

    private async Task ApplyFeedbackAsync(string orchestratorId, string taskId, string json)
    {
        if (_logs is null)
            return;
        var body = JsonSerializer.Deserialize<A2AFeedbackBody>(json, A2AJson.Options);
        if (body is null || string.IsNullOrWhiteSpace(body.LogId) || string.IsNullOrWhiteSpace(body.StepId))
            return;
        var ok = await _logs.SetFeedbackAsync(body.LogId, body.StepId, body.Verdict, body.Note, _auth?.Email);
        if (!ok)
            return;
        var step = _logs.Get(body.LogId)?.Steps?.FirstOrDefault(s => s.StepId == body.StepId);
        if (step is null)
            return;
        await SendFrameAsync(orchestratorId, taskId, A2AMethod.FeedbackSaved, new A2AStepBody { Step = step.Clone() });
    }

    private bool ApplyOrchestratorFrame(string method, string taskId, string json)
    {
        AgentTaskReceipt? receipt;
        lock (_gate)
            receipt = _receipts.FirstOrDefault(r => r.TaskId == taskId);
        if (receipt is null)
            return false;

        var now = DateTimeOffset.UtcNow;
        if (method == A2AMethod.Step)
        {
            var body = JsonSerializer.Deserialize<A2AStepBody>(json, A2AJson.Options);
            if (body?.Step is null || string.IsNullOrEmpty(body.Step.StepId))
                return false;
            UpsertStep(receipt, body.Step);
            receipt.LastEventUtc = now;
            receipt.Behind = false;
            if (receipt.State is A2ATaskState.Submitted or "")
                receipt.State = A2ATaskState.Working;
            if (receipt.State == A2ATaskState.Working)
                receipt.Error = null;
            return true;
        }

        if (method == A2AMethod.FeedbackSaved)
        {
            var body = JsonSerializer.Deserialize<A2AStepBody>(json, A2AJson.Options);
            if (body?.Step is null)
                return false;
            var existing = receipt.Steps.FirstOrDefault(s => s.StepId == body.Step.StepId);
            if (existing is null)
            {
                UpsertStep(receipt, body.Step);
            }
            else
            {
                existing.FeedbackVerdict = body.Step.FeedbackVerdict;
                existing.FeedbackNote = body.Step.FeedbackNote;
                existing.FeedbackBy = body.Step.FeedbackBy;
                existing.FeedbackAtUtc = body.Step.FeedbackAtUtc;
            }
            receipt.LastEventUtc = now;
            return true;
        }

        var status = JsonSerializer.Deserialize<A2AStatusBody>(json, A2AJson.Options);
        if (status is null || string.IsNullOrWhiteSpace(status.State))
            return false;
        if (IsTerminal(receipt.State) && status.State is A2ATaskState.Submitted or A2ATaskState.Working)
            return false;
        receipt.State = status.State;
        receipt.LastEventUtc = now;
        receipt.Behind = false;
        if (!string.IsNullOrEmpty(status.LogId))
            receipt.WorkerLogId = status.LogId;
        if (status.Text is not null && IsTerminal(status.State))
            receipt.ResultText = status.Text;
        if (IsTerminal(status.State))
        {
            receipt.EndedAtUtc = now;
            receipt.Error = string.IsNullOrWhiteSpace(status.Error) ? null : status.Error;
        }
        else if (status.State == A2ATaskState.Working)
        {
            receipt.Error = null;
        }
        return true;
    }

    private static void UpsertStep(AgentTaskReceipt receipt, SkillStep step)
    {
        var copy = step.Clone();
        if (copy.Text.Length > 1500)
            copy.Text = copy.Text[..1500] + "…";
        var index = receipt.Steps.FindIndex(s => s.StepId == copy.StepId);
        if (index >= 0)
            receipt.Steps[index] = copy;
        else
            receipt.Steps.Add(copy);
        if (receipt.Steps.Count > 200)
            receipt.Steps.RemoveRange(0, receipt.Steps.Count - 200);
    }

    private void Watch()
    {
        var changed = false;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var receipt in _receipts)
            {
                if (IsTerminal(receipt.State))
                    continue;
                var last = receipt.LastEventUtc == default ? receipt.StartedAtUtc : receipt.LastEventUtc;
                var silent = now - last;
                if (receipt.State == A2ATaskState.Submitted && silent > AcceptTimeout)
                {
                    receipt.State = A2ATaskState.Failed;
                    receipt.Behind = true;
                    receipt.EndedAtUtc = now;
                    receipt.Error = "The worker did not accept the task. Its log is unchanged.";
                    changed = true;
                }
                else if (receipt.State == A2ATaskState.Working && !receipt.Behind && silent > WorkingSilence)
                {
                    receipt.Behind = true;
                    receipt.Error = "No recent update. The worker may still be running; its log is the record of the run.";
                    changed = true;
                }
            }
        }
        if (!changed)
            return;
        _ = SaveReceiptsAsync();
        Changed?.Invoke();
    }

    private async Task<bool> SendStatusAsync(
        string to,
        string taskId,
        string state,
        string? text,
        string? error,
        string? logId) =>
        await SendFrameAsync(to, taskId, A2AMethod.Status, new A2AStatusBody
        {
            State = state,
            Text = text,
            Error = error,
            LogId = logId
        });

    private async Task<bool> SendStepAsync(string to, string taskId, SkillStep step)
    {
        var copy = step.Clone();
        if (copy.Text.Length > 1500)
            copy.Text = copy.Text[..1500] + "…";
        return await SendFrameAsync(to, taskId, A2AMethod.Step, new A2AStepBody { Step = copy });
    }

    private async Task<bool> SendFrameAsync(string to, string taskId, string method, object body)
    {
        // Delivery is the SignalR send. Callers do not wait for the peer to ack, so a tool call
        // is not stuck on the orchestrator being awake.
        if (_sync is null || string.IsNullOrWhiteSpace(to))
            return false;
        string cipher;
        try
        {
            var encrypted = await EncryptAsync(JsonSerializer.Serialize(body, A2AJson.Options));
            if (encrypted is null)
            {
                LastError = "Sign in so this device can encrypt agent results.";
                return false;
            }
            cipher = encrypted;
        }
        catch
        {
            LastError = "Could not encrypt the agent message.";
            return false;
        }

        var envelope = new A2AEnvelope
        {
            TaskId = taskId,
            Seq = Interlocked.Increment(ref _seq),
            Method = method,
            Cipher = cipher
        };
        try
        {
            return await _sync.SendA2AAsync(to, JsonSerializer.Serialize(envelope, A2AJson.Options));
        }
        catch
        {
            LastError = "Could not reach that device.";
            return false;
        }
    }

    private async Task<string?> EncryptAsync(string plaintext)
    {
        var key = _auth?.LocalEncryptionKeyB64;
        if (_crypto is null || string.IsNullOrEmpty(key))
            return null;
        return await _crypto.EncryptAsync(key, plaintext);
    }

    private async Task<string?> DecryptAsync(string cipher)
    {
        var key = _auth?.LocalEncryptionKeyB64;
        if (_crypto is null || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(cipher))
            return null;
        return await _crypto.DecryptAsync(key, cipher);
    }

    private async Task SaveReceiptsAsync()
    {
        if (_prefs is null)
            return;
        string json;
        lock (_gate)
            json = JsonSerializer.Serialize(_receipts, A2AJson.Options);
        try { await _prefs.SetStringAsync(ReceiptsPrefKey, json); }
        catch { /* the in-memory receipt is still on screen */ }
    }

    private void DetachHandlers()
    {
        if (_sync is not null && _onSyncChanged is not null)
            _sync.OnChanged -= _onSyncChanged;
        if (_sync is not null && _onFrame is not null)
            _sync.OnA2AFrameReceived -= _onFrame;
        if (_skills is not null && _onSkillsChanged is not null)
            _skills.Changed -= _onSkillsChanged;
        _onSyncChanged = null;
        _onFrame = null;
        _onSkillsChanged = null;
        _watch?.Dispose();
        _watch = null;
    }

    private static bool IsTerminal(string? state) =>
        state is A2ATaskState.Completed or A2ATaskState.Failed or A2ATaskState.Canceled;

    private static string OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return string.Join(' ', value.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string Trim(string? value, int max)
    {
        var text = value ?? "";
        return text.Length <= max ? text : text[..max];
    }
}
