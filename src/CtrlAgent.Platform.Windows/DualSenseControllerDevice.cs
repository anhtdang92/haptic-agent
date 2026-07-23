using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CtrlAgent.Controllers.DualSense;
using CtrlAgent.Core;

namespace CtrlAgent.Platform.Windows;

/// <summary>
/// PS5 DualSense over raw HID: input reports become normalized events, rumble
/// goes out as output reports, and the lightbar is set to the CtrlAgent cyan.
/// Face buttons map positionally (Cross→A, Circle→B, Square→X, Triangle→Y);
/// on the DualSense Edge the rear paddles and Fn buttons map to the four
/// paddle controls. Wire format is community-documented and pending
/// verification against real hardware.
/// </summary>
public sealed class DualSenseControllerDevice : IControllerDevice
{
    private const float AnalogEpsilon = 0.015f;

    private static readonly (DualSenseButtons Button, ControllerControl Control)[] ButtonMap =
    [
        (DualSenseButtons.Cross, ControllerControl.A),
        (DualSenseButtons.Circle, ControllerControl.B),
        (DualSenseButtons.Square, ControllerControl.X),
        (DualSenseButtons.Triangle, ControllerControl.Y),
        (DualSenseButtons.DPadUp, ControllerControl.DPadUp),
        (DualSenseButtons.DPadDown, ControllerControl.DPadDown),
        (DualSenseButtons.DPadLeft, ControllerControl.DPadLeft),
        (DualSenseButtons.DPadRight, ControllerControl.DPadRight),
        (DualSenseButtons.L1, ControllerControl.LeftShoulder),
        (DualSenseButtons.R1, ControllerControl.RightShoulder),
        (DualSenseButtons.Create, ControllerControl.View),
        (DualSenseButtons.Options, ControllerControl.Menu),
        (DualSenseButtons.L3, ControllerControl.LeftThumbstickButton),
        (DualSenseButtons.R3, ControllerControl.RightThumbstickButton),
        (DualSenseButtons.LeftPaddle, ControllerControl.PaddleLeft1),
        (DualSenseButtons.LeftFunction, ControllerControl.PaddleLeft2),
        (DualSenseButtons.RightPaddle, ControllerControl.PaddleRight1),
        (DualSenseButtons.RightFunction, ControllerControl.PaddleRight2),
    ];

    private readonly FileStream _stream;
    private readonly int _inputReportLength;
    private readonly int _outputReportLength;
    private readonly bool _isBluetooth;
    private readonly bool _isEdge;
    private readonly Channel<ControllerInputEvent> _events = Channel.CreateUnbounded<ControllerInputEvent>();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;
    private byte _sequence;
    private bool _disposed;
    private volatile bool _isConnected = true;

    private DualSenseControllerDevice(FileStream stream, DualSenseHidNative.HidDeviceInfo info)
    {
        _stream = stream;
        _inputReportLength = info.InputReportLength;
        _outputReportLength = info.OutputReportLength;
        _isBluetooth = info.InputReportLength > 64;
        _isEdge = info.ProductId == DualSenseProtocol.DualSenseEdgeProductId;
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token), CancellationToken.None);
    }

    public string Id => _isEdge ? "dualsense:edge" : "dualsense:primary";

    public string DisplayName => _isEdge
        ? $"DualSense Edge ({Transport})"
        : $"DualSense ({Transport})";

    public ControllerCapabilities Capabilities => new(
        HasFourPaddles: _isEdge,
        HasLowFrequencyRumble: true,
        HasHighFrequencyRumble: true,
        HasLeftTriggerRumble: false,
        HasRightTriggerRumble: false);

    public bool IsConnected => _isConnected;

    private string Transport => _isBluetooth ? "Bluetooth" : "USB";

    /// <summary>Returns an opened DualSense device, or null when none is present.</summary>
    public static DualSenseControllerDevice? TryCreate()
    {
        try
        {
            foreach (var info in DualSenseHidNative.EnumerateDevices())
            {
                if (!DualSenseProtocol.IsSupported(info.VendorId, info.ProductId) ||
                    info.UsagePage != 0x01 ||
                    info.Usage != 0x05)
                {
                    continue;
                }

                var handle = DualSenseHidNative.OpenDevice(info.Path);
                var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);

                if (info.InputReportLength > 64)
                {
                    // Over Bluetooth the pad sends compact reports until a
                    // feature report is requested; this kicks it into full mode.
                    DualSenseHidNative.TryGetFeature(handle, 0x05, info.FeatureReportLength);
                }

                var device = new DualSenseControllerDevice(stream, info);
                _ = device.SendOutputBestEffortAsync(0f, 0f);
                return device;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public async IAsyncEnumerable<ControllerInputEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await foreach (var inputEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return inputEvent;
        }
    }

    public async ValueTask PlayAsync(HapticPattern pattern, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pattern);

        try
        {
            do
            {
                foreach (var frame in pattern.Frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await SendOutputAsync(frame.LowFrequency, frame.HighFrequency, cancellationToken)
                        .ConfigureAwait(false);
                    await Task.Delay(frame.Duration, cancellationToken).ConfigureAwait(false);
                }
            }
            while (pattern.Loop && !cancellationToken.IsCancellationRequested);
        }
        finally
        {
            await SendOutputBestEffortAsync(0f, 0f).ConfigureAwait(false);
        }
    }

    public async ValueTask StopHapticsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SendOutputAsync(0f, 0f, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isConnected = false;
        await SendOutputBestEffortAsync(0f, 0f).ConfigureAwait(false);
        _lifetime.Cancel();

        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _stream.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
        _events.Writer.TryComplete();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Max(_inputReportLength, 16)];
        var hasPrevious = false;
        DualSenseInputState previous = default;

        Publish(ControllerControl.None, ControllerInputEventKind.Connected, 1f);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (!DualSenseProtocol.TryParseInput(buffer.AsSpan(0, read), out var current))
                {
                    continue;
                }

                EmitDiff(previous, current, hasPrevious);
                previous = current;
                hasPrevious = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Unplugged or handle closed: end the stream so the host recycles.
        }
        finally
        {
            _isConnected = false;
            Publish(ControllerControl.None, ControllerInputEventKind.Disconnected, 0f);
            _events.Writer.TryComplete();
        }
    }

    private void EmitDiff(DualSenseInputState previous, DualSenseInputState current, bool hasPrevious)
    {
        foreach (var (button, control) in ButtonMap)
        {
            var wasPressed = hasPrevious && previous.Buttons.HasFlag(button);
            var isPressed = current.Buttons.HasFlag(button);
            if (wasPressed == isPressed)
            {
                continue;
            }

            Publish(
                control,
                isPressed ? ControllerInputEventKind.Pressed : ControllerInputEventKind.Released,
                isPressed ? 1f : 0f);
        }

        EmitAxis(ControllerControl.LeftThumbstickX, Previous(previous.LeftStickX), DualSenseProtocol.NormalizeStick(current.LeftStickX), hasPrevious);
        EmitAxis(ControllerControl.LeftThumbstickY, Previous((byte)(255 - previous.LeftStickY)), DualSenseProtocol.NormalizeStick((byte)(255 - current.LeftStickY)), hasPrevious);
        EmitAxis(ControllerControl.RightThumbstickX, Previous(previous.RightStickX), DualSenseProtocol.NormalizeStick(current.RightStickX), hasPrevious);
        EmitAxis(ControllerControl.RightThumbstickY, Previous((byte)(255 - previous.RightStickY)), DualSenseProtocol.NormalizeStick((byte)(255 - current.RightStickY)), hasPrevious);
        EmitAxis(ControllerControl.LeftTrigger, hasPrevious ? DualSenseProtocol.NormalizeTrigger(previous.LeftTrigger) : 0f, DualSenseProtocol.NormalizeTrigger(current.LeftTrigger), hasPrevious);
        EmitAxis(ControllerControl.RightTrigger, hasPrevious ? DualSenseProtocol.NormalizeTrigger(previous.RightTrigger) : 0f, DualSenseProtocol.NormalizeTrigger(current.RightTrigger), hasPrevious);

        float Previous(byte raw) => hasPrevious ? DualSenseProtocol.NormalizeStick(raw) : 0f;
    }

    private void EmitAxis(ControllerControl control, float previous, float current, bool hasPrevious)
    {
        if (hasPrevious && Math.Abs(current - previous) < AnalogEpsilon)
        {
            return;
        }

        Publish(control, ControllerInputEventKind.ValueChanged, current);
    }

    private void Publish(ControllerControl control, ControllerInputEventKind kind, float value) =>
        _events.Writer.TryWrite(new ControllerInputEvent(Id, control, kind, value, DateTimeOffset.UtcNow));

    private async ValueTask SendOutputAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken)
    {
        // Lightbar stays CtrlAgent cyan on every packet.
        var report = _isBluetooth
            ? DualSenseProtocol.BuildBluetoothOutput(_sequence++, lowFrequency, highFrequency, 0x00, 0xD4, 0xFF)
            : DualSenseProtocol.BuildUsbOutput(lowFrequency, highFrequency, 0x00, 0xD4, 0xFF);

        if (report.Length != _outputReportLength && _outputReportLength > 0)
        {
            var sized = new byte[_outputReportLength];
            Array.Copy(report, sized, Math.Min(report.Length, sized.Length));
            report = sized;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(report, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask SendOutputBestEffortAsync(float lowFrequency, float highFrequency)
    {
        try
        {
            await SendOutputAsync(lowFrequency, highFrequency, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
