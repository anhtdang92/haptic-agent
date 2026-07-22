using CtrlAgent.Adapters.Codex;
using CtrlAgent.Adapters.Mock;
using CtrlAgent.Core;
using CtrlAgent.Platform.Windows;

namespace CtrlAgent.Gui;

/// <summary>
/// Runs the controller-session and agent-event loops behind the GUI, mirroring
/// the console host's wiring. Events are raised on background threads; the
/// view model marshals them onto the UI thread.
/// </summary>
public sealed class HostEngine : IAsyncDisposable
{
    private readonly GuiOptions _options;
    private readonly ControllerProfile _profile;
    private readonly MappingEngine _mapping;
    private readonly HapticSchedulerHub _haptics = new();
    private readonly FeedbackRouter _feedback = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();

    private WindowsControllerProvider? _provider;
    private IAgentAdapter? _adapter;
    private Task? _controllerTask;
    private Task? _agentTask;
    private string? _pendingSessionId;
    private string? _pendingRequestId;
    private bool _disposed;

    public HostEngine(GuiOptions options, ControllerProfile profile)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _mapping = new MappingEngine(profile);
    }

    public event Action<string>? LogEmitted;

    public event Action<string>? ControllerStatusChanged;

    public event Action<string>? AgentStateChanged;

    /// <summary>Message of the pending approval request, or null when cleared.</summary>
    public event Action<string?>? PendingApprovalChanged;

    public ControllerProfile Profile => _profile;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _provider = new WindowsControllerProvider(_options.GameInputBridgeExecutable);
        _adapter = _options.Agent switch
        {
            "codex" => new CodexAppServerAdapter(new AgentAdapterOptions(
                _options.WorkingDirectory,
                _options.CodexExecutable)),
            _ => new MockAgentAdapter(),
        };

        await _adapter.StartAsync(_shutdown.Token).ConfigureAwait(false);
        Log($"Agent adapter '{_adapter.Id}' started.");

        _controllerTask = Task.Run(() => RunControllerSessionsAsync(_shutdown.Token));
        _agentTask = Task.Run(() => RunAgentLoopAsync(_shutdown.Token));
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

    public Task RespondToApprovalAsync(AgentCommandKind kind)
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
            return Task.CompletedTask;
        }

        return ExecuteSafelyAsync(new AgentCommand(kind, sessionId, requestId));
    }

    public async Task PlayPatternAsync(string name)
    {
        var pattern = name switch
        {
            "working" => HapticPatternCatalog.Working,
            "approval" => HapticPatternCatalog.ApprovalRequired,
            "waiting" => HapticPatternCatalog.WaitingForInput,
            "completed" => HapticPatternCatalog.Completed,
            "error" => HapticPatternCatalog.Error,
            _ => null,
        };

        if (pattern is null)
        {
            await _haptics.StopAsync().ConfigureAwait(false);
            Log("Haptics stopped.");
            return;
        }

        Log($"Previewing haptic pattern '{pattern.Name}'.");
        await _haptics.PlayAsync(pattern).ConfigureAwait(false);
    }

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

        if (_adapter is not null)
        {
            await _adapter.DisposeAsync().ConfigureAwait(false);
        }

        if (_provider is not null)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    private async Task RunControllerSessionsAsync(CancellationToken cancellationToken)
    {
        var provider = _provider!;

        while (!cancellationToken.IsCancellationRequested)
        {
            IControllerDevice? controller = null;
            try
            {
                ControllerStatusChanged?.Invoke("Searching…");
                controller = await WaitForControllerAsync(provider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var scheduler = new HapticScheduler(controller);
            _haptics.Attach(scheduler);
            ControllerStatusChanged?.Invoke(
                $"{controller.DisplayName}{(controller.Capabilities.HasFourPaddles ? " (paddles)" : " (XInput fallback)")}");
            Log($"Controller connected: {controller.DisplayName}");

            try
            {
                await foreach (var inputEvent in controller.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (inputEvent.Kind != ControllerInputEventKind.ValueChanged)
                    {
                        Log($"[controller] {inputEvent.Kind} {inputEvent.Control}");
                    }

                    foreach (var command in _mapping.Process(inputEvent))
                    {
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

    private static async Task<IControllerDevice> WaitForControllerAsync(
        WindowsControllerProvider provider,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var controller = await provider.GetPrimaryControllerAsync(cancellationToken).ConfigureAwait(false);
            if (controller is { IsConnected: true })
            {
                return controller;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAgentLoopAsync(CancellationToken cancellationToken)
    {
        var adapter = _adapter!;

        try
        {
            await foreach (var agentEvent in adapter.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                Log($"[agent] {agentEvent.State}: {agentEvent.Message}");
                AgentStateChanged?.Invoke(agentEvent.State.ToString());

                if (agentEvent.State is AgentStateKind.ApprovalRequired or AgentStateKind.WaitingForInput)
                {
                    lock (_sync)
                    {
                        _pendingSessionId = agentEvent.SessionId;
                        _pendingRequestId = agentEvent.RequestId;
                    }

                    _mapping.SetPendingApproval(agentEvent.SessionId, agentEvent.RequestId);
                    PendingApprovalChanged?.Invoke(agentEvent.Message ?? "Approval required.");
                }
                else if (ShouldClearPendingRequest(agentEvent))
                {
                    lock (_sync)
                    {
                        _pendingSessionId = null;
                        _pendingRequestId = null;
                    }

                    _mapping.SetPendingApproval(null, null);
                    PendingApprovalChanged?.Invoke(null);
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

    private async Task ExecuteSafelyAsync(AgentCommand command)
    {
        var adapter = _adapter;
        if (adapter is null)
        {
            return;
        }

        try
        {
            await adapter.ExecuteAsync(command, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log($"{command.Kind} failed: {exception.Message}");
        }
    }

    private void Log(string message) =>
        LogEmitted?.Invoke($"{DateTimeOffset.Now:HH:mm:ss} {message}");

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
            // Shutdown must never throw out of DisposeAsync.
        }
    }
}
