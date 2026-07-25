using CtrlAgent.Core;

namespace CtrlAgent.Hosting;

/// <summary>Immutable description of the currently connected controller.</summary>
public sealed record ControllerSnapshot(string Id, string DisplayName, ControllerCapabilities Capabilities);

public sealed record HostEngineOptions(string DefaultPrompt, bool LogAnalogChanges = false);

/// <summary>
/// The shared host: runs the controller-session loop and the agent-event loop
/// on top of any <see cref="IControllerProvider"/> and <see cref="IAgentAdapter"/>.
/// Both the console host and the GUI build on this engine, so behavior changes
/// land in one place. The engine owns (and disposes) the provider and adapter
/// it is given. Events are raised on background threads; UI consumers must
/// marshal to their own thread.
/// </summary>
public sealed class HostEngine : IAsyncDisposable
{
    private readonly IControllerProvider _provider;
    private readonly IAgentAdapter _adapter;
    private readonly HostEngineOptions _options;
    private readonly HapticSchedulerHub _haptics = new();
    private readonly FeedbackRouter _feedback = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();

    private Task? _controllerTask;
    private Task? _agentTask;
    private ControllerProfile _profile;
    private MappingEngine _mapping;
    private const int MaxQueuedPrompts = 5;

    private string? _pendingSessionId;
    private string? _pendingRequestId;
    private ControllerCapabilities? _lastCapabilities;
    private volatile bool _inputCaptured;
    private readonly Queue<AgentCommand> _promptQueue = new();
    private bool _agentBusy;
    private bool _disposed;

    public HostEngine(
        IControllerProvider provider,
        IAgentAdapter adapter,
        ControllerProfile profile,
        HostEngineOptions options)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _mapping = new MappingEngine(profile);
    }

    public event Action<string>? LogEmitted;

    public event Action<ControllerSnapshot>? ControllerConnected;

    /// <summary>Human-readable status: "Searching…", "Disconnected — waiting…".</summary>
    public event Action<string>? ControllerStatusChanged;

    /// <summary>Every raw controller input event, including analog changes
    /// (drives live input visualization).</summary>
    public event Action<ControllerInputEvent>? ControllerInputReceived;

    public event Action<AgentEvent>? AgentEventReceived;

    /// <summary>Message of the pending approval request, or null when cleared.</summary>
    public event Action<string?>? PendingApprovalChanged;

    /// <summary>Raised after a new profile is applied at runtime.</summary>
    public event Action<ControllerProfile>? ProfileApplied;

    /// <summary>Raised when the queued-prompt count changes.</summary>
    public event Action<int>? PromptQueueChanged;

    public ControllerProfile Profile => _profile;

    public string AdapterId => _adapter.Id;

    /// <summary>True while a controller-navigated UI owns the input.</summary>
    public bool InputCaptured => _inputCaptured;

    /// <summary>
    /// Routes controller input to the UI instead of the mapping engine (raw
    /// events keep flowing through <see cref="ControllerInputReceived"/>).
    /// Approval commands still execute — a fullscreen menu must never take
    /// away the ability to answer a pending approval from the controller.
    /// </summary>
    public void SetInputCapture(bool captured) => _inputCaptured = captured;

    /// <summary>Commands that bypass input capture (the approval family).</summary>
    public static bool IsAllowedWhileCaptured(AgentCommandKind kind) => kind is
        AgentCommandKind.ApproveOnce or
        AgentCommandKind.ApproveForSession or
        AgentCommandKind.Decline or
        AgentCommandKind.Cancel;

    /// <summary>States during which a new prompt waits in the queue.</summary>
    public static bool IsBusyState(AgentStateKind state) => state is
        AgentStateKind.Working or
        AgentStateKind.ApprovalRequired or
        AgentStateKind.WaitingForInput;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _adapter.StartAsync(cancellationToken).ConfigureAwait(false);
        Log($"Agent adapter '{_adapter.Id}' started.");

        _controllerTask = Task.Run(() => RunControllerSessionsAsync(_shutdown.Token), CancellationToken.None);
        _agentTask = Task.Run(() => RunAgentLoopAsync(_shutdown.Token), CancellationToken.None);
    }

    public Task SubmitPromptAsync(string? text) =>
        ExecuteSafelyAsync(new AgentCommand(
            AgentCommandKind.SubmitPrompt,
            Text: string.IsNullOrWhiteSpace(text) ? _options.DefaultPrompt : text));

    public Task InterruptAsync() =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.Interrupt));

    public Task NewSessionAsync() =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.NewSession));

    public Task ReviewChangesAsync() =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.ReviewChanges));

    /// <summary>Switches the agent's permission mode (adapter-defined names,
    /// e.g. Claude Code's "default"/"plan"/"acceptEdits").</summary>
    public Task SetPermissionModeAsync(string mode) =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.SetPermissionMode, Text: mode));

    /// <summary>Compacts the conversation, freeing context.</summary>
    public Task CompactContextAsync() =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.CompactContext));

    /// <summary>Switches the model (alias or full id).</summary>
    public Task SetModelAsync(string model) =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.SetModel, Text: model));

    /// <summary>Switches reasoning effort.</summary>
    public Task SetEffortAsync(string effort) =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.SetEffort, Text: effort));

    /// <summary>Steps to the next model in the cycle.</summary>
    public Task CycleModelAsync() =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.CycleModel));

    /// <summary>Steps to the next reasoning-effort level.</summary>
    public Task CycleEffortAsync() =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.CycleEffort));

    /// <summary>The model and effort last requested through this engine, so
    /// UIs can label the current position of each cycle. Null until set —
    /// the agent's own default is whatever it started with.</summary>
    public string? CurrentModel { get; private set; }

    public string? CurrentEffort { get; private set; }

    /// <summary>Raised when a cycle advances, with (model, effort).</summary>
    public event Action<string?, string?>? SessionSettingsChanged;

    public Task NextSessionAsync() =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.NextSession));

    public Task PreviousSessionAsync() =>
        ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.PreviousSession));

    /// <summary>
    /// Answers the pending approval request. Returns false when nothing is
    /// pending (no command is sent).
    /// </summary>
    public async Task<bool> RespondToApprovalAsync(AgentCommandKind kind)
    {
        string? sessionId;
        string? requestId;
        lock (_sync)
        {
            sessionId = _pendingSessionId;
            requestId = _pendingRequestId;
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            Log("No approval request is pending.");
            return false;
        }

        await ExecuteSafelyAsync(new AgentCommand(kind, sessionId, requestId)).ConfigureAwait(false);
        return true;
    }

    /// <summary>Cancels the pending approval if any, otherwise the active turn.</summary>
    public async Task CancelAsync()
    {
        if (!await RespondToApprovalAsync(AgentCommandKind.Cancel).ConfigureAwait(false))
        {
            await ExecuteSafelyAsync(new AgentCommand(AgentCommandKind.Cancel)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Swaps the active profile at runtime. Returns false (with the problem
    /// list) when the profile fails validation; the current profile stays
    /// active. The pending-approval state carries over to the new mapping.
    /// </summary>
    public bool TryApplyProfile(ControllerProfile profile, out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(profile);

        errors = ControllerProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            return false;
        }

        var mapping = new MappingEngine(profile);
        lock (_sync)
        {
            mapping.SetPendingApproval(_pendingSessionId, _pendingRequestId);
            mapping.SetDeviceCapabilities(_lastCapabilities);
            _mapping = mapping;
            _profile = profile;
        }

        Log($"Applied profile '{profile.Name}' ({profile.Bindings.Count} bindings).");
        ProfileApplied?.Invoke(profile);
        return true;
    }

    public ValueTask PlayPatternAsync(HapticPattern pattern, CancellationToken cancellationToken = default) =>
        _haptics.PlayAsync(pattern, cancellationToken);

    public ValueTask StopHapticsAsync(CancellationToken cancellationToken = default) =>
        _haptics.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();

        await AwaitQuietlyAsync(_controllerTask).ConfigureAwait(false);
        await AwaitQuietlyAsync(_agentTask).ConfigureAwait(false);

        await _adapter.DisposeAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task RunControllerSessionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IControllerDevice controller;
            try
            {
                ControllerStatusChanged?.Invoke("Searching…");
                controller = await WaitForControllerAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var scheduler = new HapticScheduler(controller);
            _haptics.Attach(scheduler);
            lock (_sync)
            {
                // Capability-gated profile layers follow the connected device.
                _lastCapabilities = controller.Capabilities;
                _mapping.SetDeviceCapabilities(controller.Capabilities);
            }

            ControllerConnected?.Invoke(new ControllerSnapshot(
                controller.Id,
                controller.DisplayName,
                controller.Capabilities));
            Log($"Controller connected: {controller.DisplayName}");

            try
            {
                await foreach (var inputEvent in controller.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
                {
                    ControllerInputReceived?.Invoke(inputEvent);

                    if (inputEvent.Kind != ControllerInputEventKind.ValueChanged)
                    {
                        Log($"[controller] {inputEvent.Kind} {inputEvent.Control}");
                    }
                    else if (_options.LogAnalogChanges)
                    {
                        Log($"[controller] {inputEvent.Kind} {inputEvent.Control} {inputEvent.Value:F2}");
                    }

                    foreach (var command in _mapping.Process(inputEvent))
                    {
                        if (_inputCaptured && !IsAllowedWhileCaptured(command.Kind))
                        {
                            continue;
                        }

                        var hydrated = command;
                        if (command.Kind == AgentCommandKind.SubmitPrompt && string.IsNullOrWhiteSpace(command.Text))
                        {
                            hydrated = command with { Text = _options.DefaultPrompt };
                        }

                        Log($"[command] {hydrated.Kind}");
                        await ExecuteSafelyAsync(hydrated).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log($"Controller connection lost: {exception.Message}");
            }
            finally
            {
                _haptics.Detach(scheduler);
                await scheduler.DisposeAsync().ConfigureAwait(false);
                await controller.DisposeAsync().ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                ControllerStatusChanged?.Invoke("Disconnected — waiting…");
                Log("Controller disconnected. Waiting for a controller…");
            }
        }
    }

    private async Task<IControllerDevice> WaitForControllerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var controller = await _provider.GetPrimaryControllerAsync(cancellationToken).ConfigureAwait(false);
            if (controller is { IsConnected: true })
            {
                return controller;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAgentLoopAsync(CancellationToken cancellationToken)
    {
        AgentStateKind? previousState = null;
        try
        {
            await foreach (var agentEvent in _adapter.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                // Streaming adapters publish many Working events per turn
                // (partial text). The UI wants each one (live rendering); the
                // log and the haptic loop only care about the state change.
                var isRepeatWorking =
                    agentEvent.State == AgentStateKind.Working &&
                    previousState == AgentStateKind.Working;
                previousState = agentEvent.State;

                if (!isRepeatWorking)
                {
                    Log($"[agent] {agentEvent.State}: {agentEvent.Message}");
                }

                AgentEventReceived?.Invoke(agentEvent);

                if (agentEvent.State is AgentStateKind.ApprovalRequired or AgentStateKind.WaitingForInput)
                {
                    MappingEngine mapping;
                    lock (_sync)
                    {
                        _pendingSessionId = agentEvent.SessionId;
                        _pendingRequestId = agentEvent.RequestId;
                        mapping = _mapping;
                    }

                    mapping.SetPendingApproval(agentEvent.SessionId, agentEvent.RequestId);
                    PendingApprovalChanged?.Invoke(agentEvent.Message ?? "Approval required.");
                }
                else if (ShouldClearPendingRequest(agentEvent))
                {
                    MappingEngine mapping;
                    lock (_sync)
                    {
                        _pendingSessionId = null;
                        _pendingRequestId = null;
                        mapping = _mapping;
                    }

                    mapping.SetPendingApproval(null, null);
                    PendingApprovalChanged?.Invoke(null);
                }

                await UpdateBusyAndFlushQueueAsync(agentEvent.State).ConfigureAwait(false);

                if (isRepeatWorking)
                {
                    continue;
                }

                var pattern = _feedback.Route(agentEvent);
                if (pattern is not null)
                {
                    await _haptics.PlayAsync(pattern, cancellationToken).ConfigureAwait(false);
                }
                else if (agentEvent.State == AgentStateKind.Idle)
                {
                    await _haptics.StopAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool ShouldClearPendingRequest(AgentEvent agentEvent) =>
        agentEvent.State is AgentStateKind.Completed or AgentStateKind.Error ||
        (agentEvent.State == AgentStateKind.Working && agentEvent.RequestId is not null);

    /// <summary>
    /// Turns a cycle command into the concrete set-command it means. Cycle
    /// position lives here, once, rather than in every adapter — and an
    /// adapter that never heard of cycling still handles the result.
    /// </summary>
    private AgentCommand ResolveCycle(AgentCommand command)
    {
        switch (command.Kind)
        {
            case AgentCommandKind.CycleModel:
            {
                var next = AgentModes.Next(AgentModes.ModelCycle, CurrentModel);
                CurrentModel = next;
                SessionSettingsChanged?.Invoke(CurrentModel, CurrentEffort);
                return command with { Kind = AgentCommandKind.SetModel, Text = next };
            }

            case AgentCommandKind.CycleEffort:
            {
                var next = AgentModes.Next(AgentModes.EffortCycle, CurrentEffort);
                CurrentEffort = next;
                SessionSettingsChanged?.Invoke(CurrentModel, CurrentEffort);
                return command with { Kind = AgentCommandKind.SetEffort, Text = next };
            }

            case AgentCommandKind.SetModel when command.Text is { Length: > 0 }:
                CurrentModel = command.Text;
                SessionSettingsChanged?.Invoke(CurrentModel, CurrentEffort);
                return command;

            case AgentCommandKind.SetEffort when command.Text is { Length: > 0 }:
                CurrentEffort = command.Text;
                SessionSettingsChanged?.Invoke(CurrentModel, CurrentEffort);
                return command;

            default:
                return command;
        }
    }

    private async Task ExecuteSafelyAsync(AgentCommand command)
    {
        // Prompts submitted while the agent is mid-turn wait their turn
        // (Claude-app behavior) instead of interleaving into a running one.
        if (command.Kind == AgentCommandKind.SubmitPrompt)
        {
            int? queuedCount = null;
            var dropped = false;
            lock (_sync)
            {
                if (_agentBusy)
                {
                    if (_promptQueue.Count >= MaxQueuedPrompts)
                    {
                        dropped = true;
                    }
                    else
                    {
                        _promptQueue.Enqueue(command);
                        queuedCount = _promptQueue.Count;
                    }
                }
                else
                {
                    // Optimistic: the Working event may not have landed yet,
                    // and a double-tap must queue, not double-send.
                    _agentBusy = true;
                }
            }

            if (dropped)
            {
                Log($"Prompt queue is full ({MaxQueuedPrompts}); the new prompt was dropped.");
                return;
            }

            if (queuedCount is { } count)
            {
                Log($"Agent is busy — prompt queued ({count} waiting).");
                PromptQueueChanged?.Invoke(count);
                return;
            }
        }

        await DispatchAsync(command).ConfigureAwait(false);
    }

    /// <summary>Flushes one queued prompt when the agent settles.</summary>
    private async Task UpdateBusyAndFlushQueueAsync(AgentStateKind state)
    {
        AgentCommand? next = null;
        var remaining = 0;
        lock (_sync)
        {
            _agentBusy = IsBusyState(state);
            if (!_agentBusy && _pendingRequestId is null && _promptQueue.Count > 0)
            {
                next = _promptQueue.Dequeue();
                remaining = _promptQueue.Count;
                _agentBusy = true;
            }
        }

        if (next is not null)
        {
            PromptQueueChanged?.Invoke(remaining);
            Log("Sending queued prompt.");
            await DispatchAsync(next).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(AgentCommand command)
    {
        command = ResolveCycle(command);

        try
        {
            await _adapter.ExecuteAsync(command, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log($"[error] {command.Kind} failed: {exception.Message}");
        }
    }

    private void Log(string message) => LogEmitted?.Invoke(message);

    private static async Task AwaitQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Shutdown must never throw.
        }
    }
}
