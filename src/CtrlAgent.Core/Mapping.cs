namespace CtrlAgent.Core;

public enum InputGesture
{
    Press = 0,
    Release,
    AxisThreshold,
    Tap,
    Hold,
    DoublePress,
}

public sealed record InputBinding(
    ControllerControl Control,
    InputGesture Gesture,
    AgentCommandKind Command,
    IReadOnlySet<ControllerControl>? Modifiers = null,
    float MinimumValue = 0.5f,
    string? Text = null,
    bool RequiresPendingApproval = false,
    TimeSpan? HoldDuration = null,
    TimeSpan? DoublePressWindow = null)
{
    public static readonly TimeSpan DefaultHoldDuration = TimeSpan.FromMilliseconds(400);

    public static readonly TimeSpan DefaultDoublePressWindow = TimeSpan.FromMilliseconds(300);

    public TimeSpan EffectiveHoldDuration => HoldDuration ?? DefaultHoldDuration;

    public TimeSpan EffectiveDoublePressWindow => DoublePressWindow ?? DefaultDoublePressWindow;
}

public sealed record ControllerProfile(
    string Name,
    IReadOnlyList<InputBinding> Bindings)
{
    private static readonly IReadOnlySet<ControllerControl> LeftShoulder =
        new HashSet<ControllerControl> { ControllerControl.LeftShoulder };

    private static readonly IReadOnlySet<ControllerControl> RightShoulder =
        new HashSet<ControllerControl> { ControllerControl.RightShoulder };

    public static ControllerProfile Default { get; } = new(
        "default",
        [
            new(ControllerControl.A, InputGesture.Press, AgentCommandKind.SubmitPrompt),
            new(ControllerControl.B, InputGesture.Press, AgentCommandKind.Interrupt),
            new(ControllerControl.X, InputGesture.Press, AgentCommandKind.ReviewChanges),
            new(ControllerControl.Menu, InputGesture.Press, AgentCommandKind.NewSession),
            new(ControllerControl.DPadRight, InputGesture.Press, AgentCommandKind.NextSession),
            new(ControllerControl.DPadLeft, InputGesture.Press, AgentCommandKind.PreviousSession),

            // Native Elite-paddle bindings used by the GameInput bridge.
            new(ControllerControl.PaddleLeft1, InputGesture.Press, AgentCommandKind.ApproveOnce, RequiresPendingApproval: true),
            new(ControllerControl.PaddleLeft2, InputGesture.Press, AgentCommandKind.ApproveForSession, RequiresPendingApproval: true),
            new(ControllerControl.PaddleRight1, InputGesture.Press, AgentCommandKind.Decline, RequiresPendingApproval: true),
            new(ControllerControl.PaddleRight2, InputGesture.Press, AgentCommandKind.Cancel, RequiresPendingApproval: true),

            // XInput fallback chords until the native GameInput bridge is selected.
            new(ControllerControl.A, InputGesture.Press, AgentCommandKind.ApproveOnce, RightShoulder, RequiresPendingApproval: true),
            new(ControllerControl.Y, InputGesture.Press, AgentCommandKind.ApproveForSession, RightShoulder, RequiresPendingApproval: true),
            new(ControllerControl.X, InputGesture.Press, AgentCommandKind.Decline, RightShoulder, RequiresPendingApproval: true),
            new(ControllerControl.B, InputGesture.Press, AgentCommandKind.Cancel, RightShoulder, RequiresPendingApproval: true),

            new(
                ControllerControl.A,
                InputGesture.Press,
                AgentCommandKind.SubmitPrompt,
                LeftShoulder,
                Text: "Run the test suite and fix any failures."),
        ]);
}

public sealed class MappingEngine
{
    private readonly object _sync = new();
    private readonly ControllerProfile _profile;
    private readonly HashSet<ControllerControl> _pressed = [];
    private readonly Dictionary<ControllerControl, DateTimeOffset> _pressStarted = [];
    private readonly Dictionary<ControllerControl, DateTimeOffset> _lastPress = [];
    private PendingApproval? _pendingApproval;

    public MappingEngine(ControllerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = ControllerProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Profile '{profile.Name}' is invalid: {string.Join(" ", errors)}",
                nameof(profile));
        }

        _profile = profile;
    }

    public void SetPendingApproval(string? sessionId, string? requestId)
    {
        lock (_sync)
        {
            _pendingApproval = string.IsNullOrWhiteSpace(requestId)
                ? null
                : new PendingApproval(sessionId, requestId);
        }
    }

    public IReadOnlyList<AgentCommand> Process(ControllerInputEvent inputEvent)
    {
        lock (_sync)
        {
            if (inputEvent.Kind == ControllerInputEventKind.Connected)
            {
                return [];
            }

            if (inputEvent.Kind == ControllerInputEventKind.Disconnected)
            {
                _pressed.Clear();
                _pressStarted.Clear();
                _lastPress.Clear();
                return [];
            }

            // Durations come from event timestamps so gesture resolution is
            // deterministic and clock-free.
            TimeSpan? doublePressInterval = null;
            TimeSpan? heldDuration = null;

            switch (inputEvent.Kind)
            {
                case ControllerInputEventKind.Pressed:
                    _pressed.Add(inputEvent.Control);
                    if (_lastPress.TryGetValue(inputEvent.Control, out var previousPress) &&
                        inputEvent.Timestamp - previousPress >= TimeSpan.Zero)
                    {
                        doublePressInterval = inputEvent.Timestamp - previousPress;
                    }

                    _pressStarted[inputEvent.Control] = inputEvent.Timestamp;
                    break;

                case ControllerInputEventKind.Released:
                    _pressed.Remove(inputEvent.Control);
                    if (_pressStarted.Remove(inputEvent.Control, out var pressStart) &&
                        inputEvent.Timestamp - pressStart >= TimeSpan.Zero)
                    {
                        heldDuration = inputEvent.Timestamp - pressStart;
                    }

                    break;
            }

            var structuralMatches = _profile.Bindings
                .Where(binding => StructurallyMatches(binding, inputEvent, doublePressInterval, heldDuration))
                .ToArray();

            if (inputEvent.Kind == ControllerInputEventKind.Pressed)
            {
                // A press that completed a double-press resets the sequence so
                // a third press starts a new pair; otherwise this press becomes
                // the first half of a potential double-press.
                if (structuralMatches.Any(binding => binding.Gesture == InputGesture.DoublePress))
                {
                    _lastPress.Remove(inputEvent.Control);
                }
                else
                {
                    _lastPress[inputEvent.Control] = inputEvent.Timestamp;
                }
            }

            if (structuralMatches.Length == 0)
            {
                return [];
            }

            // A chord wins over its unmodified button binding. Eligibility is
            // checked after specificity so RB+A cannot fall through to plain A
            // when no approval request is pending.
            var highestSpecificity = structuralMatches.Max(binding => binding.Modifiers?.Count ?? 0);

            return structuralMatches
                .Where(binding => (binding.Modifiers?.Count ?? 0) == highestSpecificity)
                .Where(IsEligible)
                .Select(binding => new AgentCommand(
                    binding.Command,
                    _pendingApproval?.SessionId,
                    _pendingApproval?.RequestId,
                    binding.Text))
                .ToArray();
        }
    }

    private bool StructurallyMatches(
        InputBinding binding,
        ControllerInputEvent inputEvent,
        TimeSpan? doublePressInterval,
        TimeSpan? heldDuration)
    {
        if (binding.Control != inputEvent.Control)
        {
            return false;
        }

        var gestureMatches = binding.Gesture switch
        {
            InputGesture.Press =>
                inputEvent.Kind == ControllerInputEventKind.Pressed,
            InputGesture.Release =>
                inputEvent.Kind == ControllerInputEventKind.Released,
            InputGesture.AxisThreshold =>
                inputEvent.Kind == ControllerInputEventKind.ValueChanged &&
                Math.Abs(inputEvent.Value) >= binding.MinimumValue,
            InputGesture.Tap =>
                inputEvent.Kind == ControllerInputEventKind.Released &&
                heldDuration is { } tapDuration &&
                tapDuration < binding.EffectiveHoldDuration,
            InputGesture.Hold =>
                inputEvent.Kind == ControllerInputEventKind.Released &&
                heldDuration is { } holdDuration &&
                holdDuration >= binding.EffectiveHoldDuration,
            InputGesture.DoublePress =>
                inputEvent.Kind == ControllerInputEventKind.Pressed &&
                doublePressInterval is { } interval &&
                interval <= binding.EffectiveDoublePressWindow,
            _ => false,
        };

        if (!gestureMatches)
        {
            return false;
        }

        return binding.Modifiers is not { Count: > 0 } || binding.Modifiers.All(_pressed.Contains);
    }

    private bool IsEligible(InputBinding binding) =>
        !binding.RequiresPendingApproval || _pendingApproval is not null;

    private sealed record PendingApproval(string? SessionId, string RequestId);
}
