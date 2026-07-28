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

/// <summary>
/// Controls when a profile layer's bindings are active. Layers let one profile
/// serve different hardware: paddle bindings that vanish on a pad without
/// paddles, and fallback chords that only exist when paddles are missing.
/// </summary>
public enum LayerActivation
{
    /// <summary>The layer is always active (same as an unlayered binding).</summary>
    Always = 0,

    /// <summary>Active only while the connected controller reports four paddles.</summary>
    RequiresPaddles,

    /// <summary>Active only while the connected controller reports no paddles.</summary>
    WithoutPaddles,
}

/// <summary>A named group of bindings with a device-capability activation rule.</summary>
public sealed record ProfileLayer(string Name, LayerActivation Activation);

public sealed record InputBinding(
    ControllerControl Control,
    InputGesture Gesture,
    AgentCommandKind Command,
    IReadOnlySet<ControllerControl>? Modifiers = null,
    float MinimumValue = 0.5f,
    string? Text = null,
    bool RequiresPendingApproval = false,
    TimeSpan? HoldDuration = null,
    TimeSpan? DoublePressWindow = null,
    string? Layer = null)
{
    public static readonly TimeSpan DefaultHoldDuration = TimeSpan.FromMilliseconds(400);

    public static readonly TimeSpan DefaultDoublePressWindow = TimeSpan.FromMilliseconds(300);

    public TimeSpan EffectiveHoldDuration => HoldDuration ?? DefaultHoldDuration;

    public TimeSpan EffectiveDoublePressWindow => DoublePressWindow ?? DefaultDoublePressWindow;
}

public sealed record ControllerProfile(
    string Name,
    IReadOnlyList<InputBinding> Bindings,
    IReadOnlyList<ProfileLayer>? Layers = null)
{
    private static readonly IReadOnlySet<ControllerControl> LeftShoulder =
        new HashSet<ControllerControl> { ControllerControl.LeftShoulder };

    private static readonly IReadOnlySet<ControllerControl> RightShoulder =
        new HashSet<ControllerControl> { ControllerControl.RightShoulder };

    /// <summary>
    /// True when a device with these capabilities can physically produce the
    /// control. Unknown capabilities optimistically allow everything, matching
    /// <see cref="MappingEngine"/>'s layer rule.
    /// </summary>
    public static bool IsControlAvailable(
        ControllerControl control,
        ControllerCapabilities? capabilities) => control switch
    {
        ControllerControl.PaddleLeft1
            or ControllerControl.PaddleLeft2
            or ControllerControl.PaddleRight1
            or ControllerControl.PaddleRight2 => capabilities?.HasFourPaddles ?? true,

        // The guide button is per-transport, so a profile that binds it is
        // only honest on hardware that delivers it. Coaching "press Xbox" on
        // a transport that never reports the press is the same dead end the
        // paddle rule exists to prevent.
        ControllerControl.Guide => capabilities?.HasGuideButton ?? true,

        _ => true,
    };

    /// <summary>
    /// True when this binding could actually fire on a device with these
    /// capabilities — its layer is active and every control it needs exists.
    /// UI that advertises shortcuts must filter through this, or it will coach
    /// inputs the hardware cannot send (Elite paddles over PC GameInput).
    /// </summary>
    public bool IsBindingReachable(InputBinding binding, ControllerCapabilities? capabilities)
    {
        if (!IsLayerActive(binding.Layer, capabilities) ||
            !IsControlAvailable(binding.Control, capabilities))
        {
            return false;
        }

        return binding.Modifiers is not { Count: > 0 } modifiers ||
            modifiers.All(modifier => IsControlAvailable(modifier, capabilities));
    }

    /// <summary>Bindings reachable on a device with these capabilities, in profile order.</summary>
    public IEnumerable<InputBinding> ReachableBindings(ControllerCapabilities? capabilities) =>
        Bindings.Where(binding => IsBindingReachable(binding, capabilities));

    private bool IsLayerActive(string? layer, ControllerCapabilities? capabilities)
    {
        if (layer is null)
        {
            return true;
        }

        var activation = Layers?.FirstOrDefault(candidate => candidate.Name == layer)?.Activation;
        return activation switch
        {
            LayerActivation.RequiresPaddles => capabilities?.HasFourPaddles ?? true,
            LayerActivation.WithoutPaddles => !(capabilities?.HasFourPaddles ?? false),
            _ => true,
        };
    }

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

            // Session controls on the inputs the pad was not using. None of
            // these can destroy work, so a plain press is safe: compacting is
            // reversible by continuing, and model/effort/plan only change how
            // the next turn runs.
            new(ControllerControl.DPadUp, InputGesture.Press, AgentCommandKind.CompactContext),
            new(
                ControllerControl.DPadDown,
                InputGesture.Press,
                AgentCommandKind.SetPermissionMode,
                Text: "plan"),
            // Y is free as a plain press (it only carries an approval as RB+Y),
            // and it is where the Mainframe already put voice, so binding it
            // makes the same button work everywhere instead of only there.
            new(ControllerControl.Y, InputGesture.Press, AgentCommandKind.StartVoicePrompt),

            // LB+X to attach: a chord, because picking files opens a modal and
            // a stray face-button press should not.
            new(
                ControllerControl.X,
                InputGesture.Press,
                AgentCommandKind.AttachFile,
                Modifiers: new HashSet<ControllerControl> { ControllerControl.LeftShoulder }),

            // Right stick reads the agent's output. A stick rather than a
            // chord because reading is the one thing you do repeatedly, and a
            // signed threshold because up and down must differ.
            new(
                ControllerControl.RightThumbstickY,
                InputGesture.AxisThreshold,
                AgentCommandKind.ScrollOutputUp,
                MinimumValue: 0.6f),
            new(
                ControllerControl.RightThumbstickY,
                InputGesture.AxisThreshold,
                AgentCommandKind.ScrollOutputDown,
                MinimumValue: -0.6f),

            new(ControllerControl.LeftThumbstickButton, InputGesture.Press, AgentCommandKind.CycleEffort),
            new(ControllerControl.RightThumbstickButton, InputGesture.Press, AgentCommandKind.CycleModel),
        ]);
}

public sealed class MappingEngine
{
    private readonly object _sync = new();
    private readonly ControllerProfile _profile;
    private readonly Dictionary<string, LayerActivation> _layerActivations;
    private readonly HashSet<ControllerControl> _pressed = [];
    private readonly Dictionary<ControllerControl, DateTimeOffset> _pressStarted = [];
    private readonly Dictionary<ControllerControl, DateTimeOffset> _lastPress = [];
    private readonly Dictionary<ControllerControl, float> _axisValues = [];
    private PendingApproval? _pendingApproval;
    private ControllerCapabilities? _capabilities;

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
        _layerActivations = (profile.Layers ?? [])
            .ToDictionary(layer => layer.Name, layer => layer.Activation, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tells the engine what the connected controller can do, which decides
    /// which profile layers are active. Until capabilities are known (null),
    /// every layer is active — the pre-layer behavior.
    /// </summary>
    public void SetDeviceCapabilities(ControllerCapabilities? capabilities)
    {
        lock (_sync)
        {
            _capabilities = capabilities;
        }
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
                _axisValues.Clear();
                return [];
            }

            // Durations come from event timestamps so gesture resolution is
            // deterministic and clock-free.
            TimeSpan? doublePressInterval = null;
            TimeSpan? heldDuration = null;
            float previousAxisValue = 0f;

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

                case ControllerInputEventKind.ValueChanged:
                    previousAxisValue = _axisValues.GetValueOrDefault(inputEvent.Control);
                    _axisValues[inputEvent.Control] = inputEvent.Value;
                    break;
            }

            var structuralMatches = _profile.Bindings
                .Where(IsLayerActive)
                .Where(binding => StructurallyMatches(
                    binding,
                    inputEvent,
                    doublePressInterval,
                    heldDuration,
                    previousAxisValue))
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
                .Where(FiresWhilePending)
                .Select(binding => new AgentCommand(
                    binding.Command,
                    _pendingApproval?.SessionId,
                    _pendingApproval?.RequestId,
                    binding.Text))
                .ToArray();
        }
    }

    /// <summary>
    /// Whether an axis just crossed its threshold, in the direction the
    /// threshold's sign asks for. A zero threshold is treated as positive.
    /// </summary>
    private static bool CrossedThreshold(float value, float previous, float threshold) =>
        threshold < 0f
            ? value <= threshold && previous > threshold
            : value >= threshold && previous < threshold;

    private bool StructurallyMatches(
        InputBinding binding,
        ControllerInputEvent inputEvent,
        TimeSpan? doublePressInterval,
        TimeSpan? heldDuration,
        float previousAxisValue)
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
            // Latches on the crossing: analog jitter past the threshold must
            // not re-fire the command.
            //
            // A NEGATIVE threshold means the negative half of the axis, so
            // "stick up" and "stick down" are separately bindable. They were
            // not before: both sides compared Math.Abs, so one binding fired
            // in both directions and a scroll-up/scroll-down pair was
            // impossible to express. Positive thresholds keep their meaning,
            // except that they no longer fire when the stick is pushed the
            // other way — which was surprising rather than useful.
            InputGesture.AxisThreshold =>
                inputEvent.Kind == ControllerInputEventKind.ValueChanged &&
                CrossedThreshold(inputEvent.Value, previousAxisValue, binding.MinimumValue),
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

    /// <summary>
    /// While an approval is pending, only the approval answers (and output
    /// scrolling — reading the diff is how you decide) may fire. Everything
    /// else is suppressed, not hidden: the HUD already showed only the answer
    /// chords in that state, but a muscle-memory LB+A still queued its preset
    /// prompt behind the user's back. A request the agent is waiting on is a
    /// modal question, and the pad answers the question or does nothing.
    /// </summary>
    private bool FiresWhilePending(InputBinding binding) =>
        _pendingApproval is null ||
        binding.RequiresPendingApproval ||
        binding.Command is AgentCommandKind.ScrollOutputUp or AgentCommandKind.ScrollOutputDown;

    private bool IsLayerActive(InputBinding binding)
    {
        if (binding.Layer is null ||
            !_layerActivations.TryGetValue(binding.Layer, out var activation))
        {
            return true;
        }

        return activation switch
        {
            LayerActivation.RequiresPaddles => _capabilities?.HasFourPaddles ?? true,
            LayerActivation.WithoutPaddles => !(_capabilities?.HasFourPaddles ?? false),
            _ => true,
        };
    }

    private sealed record PendingApproval(string? SessionId, string RequestId);
}
