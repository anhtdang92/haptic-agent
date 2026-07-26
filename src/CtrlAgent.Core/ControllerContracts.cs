namespace CtrlAgent.Core;

public enum ControllerControl
{
    None = 0,
    Menu,
    View,
    /// <summary>
    /// The Xbox/Guide button, or PlayStation's PS button. Reported only on
    /// transports that expose it: XInput's undocumented ordinal-100 entry
    /// point, and raw-HID DualSense. Steam and the Xbox Game Bar also hook
    /// this button globally, so a press may never reach us.
    /// </summary>
    Guide,
    A,
    B,
    X,
    Y,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    LeftShoulder,
    RightShoulder,
    LeftThumbstickButton,
    RightThumbstickButton,
    LeftTrigger,
    RightTrigger,
    LeftThumbstickX,
    LeftThumbstickY,
    RightThumbstickX,
    RightThumbstickY,
    PaddleLeft1,
    PaddleLeft2,
    PaddleRight1,
    PaddleRight2,
}

public enum ControllerInputEventKind
{
    Pressed = 0,
    Released,
    ValueChanged,
    Connected,
    Disconnected,
}

public sealed record ControllerInputEvent(
    string DeviceId,
    ControllerControl Control,
    ControllerInputEventKind Kind,
    float Value,
    DateTimeOffset Timestamp);

public sealed record ControllerCapabilities(
    bool HasFourPaddles,
    bool HasLowFrequencyRumble,
    bool HasHighFrequencyRumble,
    bool HasLeftTriggerRumble,
    bool HasRightTriggerRumble,
    /// <summary>
    /// Whether this transport can deliver the Xbox/PS (Guide) button.
    /// Per-transport and not guessable from the pad: raw-HID DualSense always
    /// reports it, XInput only through the undocumented ordinal-100 entry
    /// point, and the GameInput bridge only when the device advertises
    /// <c>GameInputSystemButtonGuide</c> and background guide access was
    /// granted. Defaults to true so a device that says nothing keeps its
    /// bindings visible — the same "assume reachable unless told otherwise"
    /// rule the paddle flag uses.
    /// </summary>
    bool HasGuideButton = true);

public interface IControllerProvider : IAsyncDisposable
{
    ValueTask<IControllerDevice?> GetPrimaryControllerAsync(
        CancellationToken cancellationToken = default);
}

public interface IControllerDevice : IAsyncDisposable
{
    string Id { get; }

    string DisplayName { get; }

    ControllerCapabilities Capabilities { get; }

    bool IsConnected { get; }

    IAsyncEnumerable<ControllerInputEvent> ReadEventsAsync(
        CancellationToken cancellationToken = default);

    ValueTask PlayAsync(
        HapticPattern pattern,
        CancellationToken cancellationToken = default);

    ValueTask StopHapticsAsync(
        CancellationToken cancellationToken = default);
}
