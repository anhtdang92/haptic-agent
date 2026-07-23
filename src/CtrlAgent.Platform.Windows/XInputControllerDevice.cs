using System.Runtime.CompilerServices;
using CtrlAgent.Core;

namespace CtrlAgent.Platform.Windows;

public sealed class XInputControllerDevice : IControllerDevice
{
    private static readonly (XInputButtons Button, ControllerControl Control)[] ButtonMap =
    [
        (XInputButtons.Menu, ControllerControl.Menu),
        (XInputButtons.View, ControllerControl.View),
        (XInputButtons.A, ControllerControl.A),
        (XInputButtons.B, ControllerControl.B),
        (XInputButtons.X, ControllerControl.X),
        (XInputButtons.Y, ControllerControl.Y),
        (XInputButtons.DPadUp, ControllerControl.DPadUp),
        (XInputButtons.DPadDown, ControllerControl.DPadDown),
        (XInputButtons.DPadLeft, ControllerControl.DPadLeft),
        (XInputButtons.DPadRight, ControllerControl.DPadRight),
        (XInputButtons.LeftShoulder, ControllerControl.LeftShoulder),
        (XInputButtons.RightShoulder, ControllerControl.RightShoulder),
        (XInputButtons.LeftThumb, ControllerControl.LeftThumbstickButton),
        (XInputButtons.RightThumb, ControllerControl.RightThumbstickButton),
    ];

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan DisconnectedPollInterval = TimeSpan.FromMilliseconds(250);
    private const float AnalogEpsilon = 0.015f;

    private readonly uint _userIndex;
    private bool _disposed;
    private volatile bool _isConnected;

    public XInputControllerDevice(uint userIndex)
    {
        if (userIndex > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(userIndex), "XInput supports user indexes 0 through 3.");
        }

        _userIndex = userIndex;
        _isConnected = XInputNative.GetState(_userIndex, out _) == XInputNative.ErrorSuccess;
    }

    public string Id => $"xinput:{_userIndex}";

    public string DisplayName => $"Controller (XInput {_userIndex})";

    public ControllerCapabilities Capabilities { get; } = new(
        HasFourPaddles: false,
        HasLowFrequencyRumble: true,
        HasHighFrequencyRumble: true,
        HasLeftTriggerRumble: false,
        HasRightTriggerRumble: false);

    public bool IsConnected => _isConnected;

    public async IAsyncEnumerable<ControllerInputEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        XInputState previous = default;
        var hadPrevious = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = XInputNative.GetState(_userIndex, out var current);
            var now = DateTimeOffset.UtcNow;

            if (result != XInputNative.ErrorSuccess)
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    hadPrevious = false;
                    yield return new ControllerInputEvent(
                        Id,
                        ControllerControl.None,
                        ControllerInputEventKind.Disconnected,
                        0f,
                        now);
                }

                await Task.Delay(DisconnectedPollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!_isConnected || !hadPrevious)
            {
                _isConnected = true;
                yield return new ControllerInputEvent(
                    Id,
                    ControllerControl.None,
                    ControllerInputEventKind.Connected,
                    1f,
                    now);
            }

            if (hadPrevious && current.PacketNumber == previous.PacketNumber)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var inputEvent in GetButtonEvents(previous.Gamepad, current.Gamepad, hadPrevious, now))
            {
                yield return inputEvent;
            }

            foreach (var inputEvent in GetAnalogEvents(previous.Gamepad, current.Gamepad, hadPrevious, now))
            {
                yield return inputEvent;
            }

            previous = current;
            hadPrevious = true;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask PlayAsync(
        HapticPattern pattern,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Frames.Count == 0)
        {
            return;
        }

        try
        {
            do
            {
                foreach (var frame in pattern.Frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SetRumble(frame.LowFrequency, frame.HighFrequency);
                    await Task.Delay(frame.Duration, cancellationToken).ConfigureAwait(false);
                }
            }
            while (pattern.Loop && !cancellationToken.IsCancellationRequested);
        }
        finally
        {
            SetRumble(0f, 0f);
        }
    }

    public ValueTask StopHapticsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetRumble(0f, 0f);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        SetRumble(0f, 0f);
        return ValueTask.CompletedTask;
    }

    private IEnumerable<ControllerInputEvent> GetButtonEvents(
        XInputGamepad previous,
        XInputGamepad current,
        bool hadPrevious,
        DateTimeOffset timestamp)
    {
        foreach (var (button, control) in ButtonMap)
        {
            var wasPressed = hadPrevious && ((XInputButtons)previous.Buttons & button) != 0;
            var isPressed = ((XInputButtons)current.Buttons & button) != 0;

            if (wasPressed == isPressed)
            {
                continue;
            }

            yield return new ControllerInputEvent(
                Id,
                control,
                isPressed ? ControllerInputEventKind.Pressed : ControllerInputEventKind.Released,
                isPressed ? 1f : 0f,
                timestamp);
        }
    }

    private IEnumerable<ControllerInputEvent> GetAnalogEvents(
        XInputGamepad previous,
        XInputGamepad current,
        bool hadPrevious,
        DateTimeOffset timestamp)
    {
        var values = new (ControllerControl Control, float Previous, float Current)[]
        {
            (ControllerControl.LeftTrigger, NormalizeTrigger(previous.LeftTrigger), NormalizeTrigger(current.LeftTrigger)),
            (ControllerControl.RightTrigger, NormalizeTrigger(previous.RightTrigger), NormalizeTrigger(current.RightTrigger)),
            (ControllerControl.LeftThumbstickX, NormalizeThumbstick(previous.ThumbLX), NormalizeThumbstick(current.ThumbLX)),
            (ControllerControl.LeftThumbstickY, NormalizeThumbstick(previous.ThumbLY), NormalizeThumbstick(current.ThumbLY)),
            (ControllerControl.RightThumbstickX, NormalizeThumbstick(previous.ThumbRX), NormalizeThumbstick(current.ThumbRX)),
            (ControllerControl.RightThumbstickY, NormalizeThumbstick(previous.ThumbRY), NormalizeThumbstick(current.ThumbRY)),
        };

        foreach (var value in values)
        {
            if (hadPrevious && Math.Abs(value.Current - value.Previous) < AnalogEpsilon)
            {
                continue;
            }

            yield return new ControllerInputEvent(
                Id,
                value.Control,
                ControllerInputEventKind.ValueChanged,
                value.Current,
                timestamp);
        }
    }

    private void SetRumble(float lowFrequency, float highFrequency)
    {
        var vibration = new XInputVibration
        {
            LeftMotorSpeed = ToMotorSpeed(lowFrequency),
            RightMotorSpeed = ToMotorSpeed(highFrequency),
        };

        _ = XInputNative.SetState(_userIndex, ref vibration);
    }

    private static ushort ToMotorSpeed(float value) =>
        (ushort)Math.Round(Math.Clamp(value, 0f, 1f) * ushort.MaxValue);

    private static float NormalizeTrigger(byte value) => value / 255f;

    private static float NormalizeThumbstick(short value) =>
        value >= 0 ? value / (float)short.MaxValue : value / 32768f;
}
