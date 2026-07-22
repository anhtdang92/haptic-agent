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
    public static ControllerProfile Default { get; } = new(
        "default",
        [
            new(ControllerControl.A, ControllerInputEventKind.Pressed, AgentCommandKind.SubmitPrompt),
            new(ControllerControl.B, ControllerInputEventKind.Pressed, AgentCommandKind.Interrupt),
            new(ControllerControl.X, ControllerInputEventKind.Pressed, AgentCommandKind.ReviewChanges),
            new(ControllerControl.Menu, ControllerInputEventKind.Pressed, AgentCommandKind.NewSession),
            new(ControllerControl.DPadRight, ControllerInputEventKind.Pressed, AgentCommandKind.NextSession),
            new(ControllerControl.DPadLeft, ControllerInputEventKind.Pressed, AgentCommandKind.PreviousSession),
            new(ControllerControl.PaddleLeft1, ControllerInputEventKind.Pressed, AgentCommandKind.ApproveOnce, RequiresPendingApproval: true),
            new(ControllerControl.PaddleLeft2, ControllerInputEventKind.Pressed, AgentCommandKind.ApproveForSession, RequiresPendingApproval: true),
            new(ControllerControl.PaddleRight1, ControllerInputEventKind.Pressed, AgentCommandKind.Decline, RequiresPendingApproval: true),
            new(ControllerControl.PaddleRight2, ControllerInputEventKind.Pressed, AgentCommandKind.Cancel, RequiresPendingApproval: true),
            new(
                ControllerControl.A,
                ControllerInputEventKind.Pressed,
                AgentCommandKind.SubmitPrompt,
                new HashSet<ControllerControl> { ControllerControl.LeftShoulder },
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

        var commands = new List<AgentCommand>();

        foreach (var binding in _profile.Bindings)
        {
            if (binding.Control != inputEvent.Control || binding.EventKind != inputEvent.Kind)
            {
                continue;
            }

            if (inputEvent.Kind == ControllerInputEventKind.ValueChanged &&
                Math.Abs(inputEvent.Value) < binding.MinimumValue)
            {
                continue;
            }

            if (binding.Modifiers is { Count: > 0 } && !binding.Modifiers.All(_pressed.Contains))
            {
                continue;
            }

            if (binding.RequiresPendingApproval && _pendingApproval is null)
            {
                continue;
            }

            commands.Add(new AgentCommand(
                binding.Command,
                _pendingApproval?.SessionId,
                _pendingApproval?.RequestId,
                binding.Text));
        }

        return commands;
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
