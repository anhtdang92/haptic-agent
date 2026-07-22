namespace HapticAgent.Core;

public sealed record InputBinding(
    ControllerControl Control,
    ControllerInputEventKind EventKind,
    AgentCommandKind Command,
    IReadOnlySet<ControllerControl>? Modifiers = null,
    float MinimumValue = 0.5f,
    string? Text = null,
    bool RequiresPendingApproval = false);

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
            new(ControllerControl.A, ControllerInputEventKind.Pressed, AgentCommandKind.SubmitPrompt),
            new(ControllerControl.B, ControllerInputEventKind.Pressed, AgentCommandKind.Interrupt),
            new(ControllerControl.X, ControllerInputEventKind.Pressed, AgentCommandKind.ReviewChanges),
            new(ControllerControl.Menu, ControllerInputEventKind.Pressed, AgentCommandKind.NewSession),
            new(ControllerControl.DPadRight, ControllerInputEventKind.Pressed, AgentCommandKind.NextSession),
            new(ControllerControl.DPadLeft, ControllerInputEventKind.Pressed, AgentCommandKind.PreviousSession),

            // Native Elite-paddle bindings used by the GameInput bridge.
            new(ControllerControl.PaddleLeft1, ControllerInputEventKind.Pressed, AgentCommandKind.ApproveOnce, RequiresPendingApproval: true),
            new(ControllerControl.PaddleLeft2, ControllerInputEventKind.Pressed, AgentCommandKind.ApproveForSession, RequiresPendingApproval: true),
            new(ControllerControl.PaddleRight1, ControllerInputEventKind.Pressed, AgentCommandKind.Decline, RequiresPendingApproval: true),
            new(ControllerControl.PaddleRight2, ControllerInputEventKind.Pressed, AgentCommandKind.Cancel, RequiresPendingApproval: true),

            // XInput fallback chords until the native GameInput bridge is selected.
            new(ControllerControl.A, ControllerInputEventKind.Pressed, AgentCommandKind.ApproveOnce, RightShoulder, RequiresPendingApproval: true),
            new(ControllerControl.Y, ControllerInputEventKind.Pressed, AgentCommandKind.ApproveForSession, RightShoulder, RequiresPendingApproval: true),
            new(ControllerControl.X, ControllerInputEventKind.Pressed, AgentCommandKind.Decline, RightShoulder, RequiresPendingApproval: true),
            new(ControllerControl.B, ControllerInputEventKind.Pressed, AgentCommandKind.Cancel, RightShoulder, RequiresPendingApproval: true),

            new(
                ControllerControl.A,
                ControllerInputEventKind.Pressed,
                AgentCommandKind.SubmitPrompt,
                LeftShoulder,
                Text: "Run the test suite and fix any failures."),
        ]);
}

public sealed class MappingEngine
{
    private readonly ControllerProfile _profile;
    private readonly HashSet<ControllerControl> _pressed = [];
    private PendingApproval? _pendingApproval;

    public MappingEngine(ControllerProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public void SetPendingApproval(string? sessionId, string? requestId)
    {
        _pendingApproval = string.IsNullOrWhiteSpace(requestId)
            ? null
            : new PendingApproval(sessionId, requestId);
    }

    public IReadOnlyList<AgentCommand> Process(ControllerInputEvent inputEvent)
    {
        UpdatePressedState(inputEvent);

        if (inputEvent.Kind is ControllerInputEventKind.Connected or ControllerInputEventKind.Disconnected)
        {
            return [];
        }

        var matches = _profile.Bindings
            .Where(binding => Matches(binding, inputEvent))
            .ToArray();

        if (matches.Length == 0)
        {
            return [];
        }

        // A chord wins over its unmodified button binding, preventing LB+A or
        // RB+A from also firing the plain A action.
        var highestSpecificity = matches.Max(binding => binding.Modifiers?.Count ?? 0);

        return matches
            .Where(binding => (binding.Modifiers?.Count ?? 0) == highestSpecificity)
            .Select(binding => new AgentCommand(
                binding.Command,
                _pendingApproval?.SessionId,
                _pendingApproval?.RequestId,
                binding.Text))
            .ToArray();
    }

    private bool Matches(InputBinding binding, ControllerInputEvent inputEvent)
    {
        if (binding.Control != inputEvent.Control || binding.EventKind != inputEvent.Kind)
        {
            return false;
        }

        if (inputEvent.Kind == ControllerInputEventKind.ValueChanged &&
            Math.Abs(inputEvent.Value) < binding.MinimumValue)
        {
            return false;
        }

        if (binding.Modifiers is { Count: > 0 } && !binding.Modifiers.All(_pressed.Contains))
        {
            return false;
        }

        return !binding.RequiresPendingApproval || _pendingApproval is not null;
    }

    private void UpdatePressedState(ControllerInputEvent inputEvent)
    {
        switch (inputEvent.Kind)
        {
            case ControllerInputEventKind.Pressed:
                _pressed.Add(inputEvent.Control);
                break;
            case ControllerInputEventKind.Released:
                _pressed.Remove(inputEvent.Control);
                break;
            case ControllerInputEventKind.Disconnected:
                _pressed.Clear();
                break;
        }
    }

    private sealed record PendingApproval(string? SessionId, string RequestId);
}
