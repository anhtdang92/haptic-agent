namespace HapticAgent.Core;

/// <summary>
/// Logical controls exposed to profiles. Platform adapters translate their
/// native input model into these values.
/// </summary>
public enum ControllerControl
{
    None = 0,
    Menu,
    View,
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

/// <summary>
/// A timestamped input change. Digital controls normally use values 0 or 1;
/// analog controls use the adapter's normalized range.
/// </summary>
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
    bool HasRightTriggerRumble);

public interface IControllerDevice : IAsyncDisposable
{
    string Id { get; }

    string DisplayName { get; }

    ControllerCapabilities Capabilities { get; }

    IAsyncEnumerable<ControllerInputEvent> ReadEventsAsync(
        CancellationToken cancellationToken = default);

    ValueTask PlayAsync(
        HapticPattern pattern,
        CancellationToken cancellationToken = default);

    ValueTask StopHapticsAsync(
        CancellationToken cancellationToken = default);
}
