using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CtrlAgent.Core;

namespace CtrlAgent.Platform.Windows;

public sealed class GameInputBridgeControllerDevice : IControllerDevice
{
    private readonly Process _process;
    private readonly Channel<ControllerInputEvent> _events = Channel.CreateUnbounded<ControllerInputEvent>();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdoutLoop;
    private readonly Task _stderrLoop;
    private bool _disposed;
    private volatile bool _isConnected;
    private volatile string _displayName = GenericName;

    private GameInputBridgeControllerDevice(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += HandleExited;

        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start the GameInput bridge.");
        }

        _process.StandardInput.AutoFlush = true;
        _stdoutLoop = Task.Run(() => ReadStdoutAsync(_lifetime.Token), CancellationToken.None);
        _stderrLoop = Task.Run(() => DrainStderrAsync(_lifetime.Token), CancellationToken.None);
    }

    public string Id => "gameinput:primary";

    /// <summary>
    /// The connected pad's name. GameInput reports a display name for some
    /// devices and nothing at all for others, so this resolves in order:
    /// the reported name, a known vendor/product pair, then a generic label.
    /// </summary>
    public string DisplayName => _displayName;

    public ControllerCapabilities Capabilities { get; private set; } = new(
        HasFourPaddles: true,
        HasLowFrequencyRumble: true,
        HasHighFrequencyRumble: true,
        HasLeftTriggerRumble: true,
        HasRightTriggerRumble: true);

    public bool IsConnected => _isConnected;

    /// <summary>
    /// True when this device can no longer produce events (disposed, or the
    /// bridge process died). The provider uses this to re-resolve a controller.
    /// </summary>
    public bool IsDefunct
    {
        get
        {
            if (_disposed)
            {
                return true;
            }

            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public static async ValueTask<GameInputBridgeControllerDevice> StartAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The GameInput bridge executable was not found.", executablePath);
        }

        var device = new GameInputBridgeControllerDevice(executablePath);
        try
        {
            await device._ready.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return device;
        }
        catch
        {
            await device.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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

    public async ValueTask PlayAsync(
        HapticPattern pattern,
        CancellationToken cancellationToken = default)
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
                    await SendAsync(
                        new
                        {
                            type = "rumble",
                            low = frame.LowFrequency,
                            high = frame.HighFrequency,
                            leftTrigger = frame.LeftTrigger,
                            rightTrigger = frame.RightTrigger,
                        },
                        cancellationToken).ConfigureAwait(false);
                    await Task.Delay(frame.Duration, cancellationToken).ConfigureAwait(false);
                }
            }
            while (pattern.Loop && !cancellationToken.IsCancellationRequested);
        }
        finally
        {
            await SendStopBestEffortAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask StopHapticsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SendAsync(new { type = "stop" }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await SendStopBestEffortAsync().ConfigureAwait(false);
        _lifetime.Cancel();

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await AwaitLoopAsync(_stdoutLoop).ConfigureAwait(false);
        await AwaitLoopAsync(_stderrLoop).ConfigureAwait(false);

        _process.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
        _events.Writer.TryComplete();
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(payload);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SendStopBestEffortAsync()
    {
        if (_process.HasExited)
        {
            return;
        }

        try
        {
            await SendAsync(new { type = "stop" }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task ReadStdoutAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                ProcessBridgeMessage(document.RootElement);
            }
            catch (JsonException)
            {
                // Ignore malformed native diagnostic output; stderr is drained separately.
            }
        }
    }

    private async Task DrainStderrAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
        }
    }

    private void ProcessBridgeMessage(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var type = typeElement.GetString();
        var timestamp = DateTimeOffset.UtcNow;

        switch (type)
        {
            case "ready":
                Capabilities = new ControllerCapabilities(
                    ReadBoolean(root, "hasFourPaddles", true),
                    ReadBoolean(root, "hasLowFrequencyRumble", true),
                    ReadBoolean(root, "hasHighFrequencyRumble", true),
                    ReadBoolean(root, "hasLeftTriggerRumble", true),
                    ReadBoolean(root, "hasRightTriggerRumble", true));
                _ready.TrySetResult();
                break;

            case "connected":
                // Name before the connected flag: the provider hands the device
                // to HostEngine as soon as IsConnected flips, and the engine
                // snapshots DisplayName immediately. Assigning in the other
                // order races and reports the generic fallback.
                _displayName = ResolveDisplayName(root);
                _isConnected = true;
                Publish(ControllerControl.None, ControllerInputEventKind.Connected, 1f, timestamp);
                break;

            case "disconnected":
                _isConnected = false;
                Publish(ControllerControl.None, ControllerInputEventKind.Disconnected, 0f, timestamp);
                break;

            case "button":
                if (TryReadControl(root, out var buttonControl))
                {
                    var pressed = ReadBoolean(root, "pressed", false);
                    Publish(
                        buttonControl,
                        pressed ? ControllerInputEventKind.Pressed : ControllerInputEventKind.Released,
                        pressed ? 1f : 0f,
                        timestamp);
                }

                break;

            case "axis":
                if (TryReadControl(root, out var axisControl) &&
                    root.TryGetProperty("value", out var valueElement) &&
                    valueElement.TryGetSingle(out var value))
                {
                    Publish(axisControl, ControllerInputEventKind.ValueChanged, value, timestamp);
                }

                break;
        }
    }

    private void Publish(
        ControllerControl control,
        ControllerInputEventKind kind,
        float value,
        DateTimeOffset timestamp)
    {
        _events.Writer.TryWrite(new ControllerInputEvent(Id, control, kind, value, timestamp));
    }

    private void HandleExited(object? sender, EventArgs eventArgs)
    {
        _isConnected = false;
        _ready.TrySetException(new InvalidOperationException(
            $"GameInput bridge exited with code {_process.ExitCode}."));
        _events.Writer.TryComplete(new InvalidOperationException(
            $"GameInput bridge exited with code {_process.ExitCode}."));
    }

    private const string GenericName = "Controller (GameInput v3)";

    /// <summary>
    /// Known Microsoft pads, by USB product id. GameInput on the PC
    /// redistributable usually leaves <c>displayName</c> empty, so without this
    /// every Xbox controller reads as the generic label.
    /// </summary>
    private static readonly Dictionary<int, string> KnownXboxProducts = new()
    {
        [0x02D1] = "Xbox One Controller",
        [0x02DD] = "Xbox One Controller",
        [0x02E3] = "Xbox Elite Series 1",
        [0x02EA] = "Xbox One S Controller",
        [0x02FD] = "Xbox One S Controller",
        [0x0B00] = "Xbox Elite Series 2",
        [0x0B05] = "Xbox Elite Series 2",
        [0x0B12] = "Xbox Wireless Controller",
        [0x0B13] = "Xbox Wireless Controller",
        [0x0B20] = "Xbox Wireless Controller",
        [0x0B21] = "Xbox Adaptive Controller",
        // Observed on this project's Elite Series 2 over Bluetooth LE
        // (2026-07-25), where Windows reports only a generic HID name.
        [0x0B22] = "Xbox Elite Series 2",
    };

    /// <summary>
    /// Windows hands out placeholder names for Bluetooth and XINPUT-compatible
    /// pads. They are worse than useless in a status strip, so a known
    /// vendor/product pair wins over them.
    /// </summary>
    private static readonly string[] GenericNameMarkers =
    [
        "XINPUT compatible",
        "HID-compliant",
        "Bluetooth LE",
        "USB Input Device",
        "Game Controller",
    ];

    private static string ResolveDisplayName(JsonElement root)
    {
        var reported = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()?.Trim()
            : null;

        var vendorId = 0;
        var productId = 0;
        var hasIds =
            root.TryGetProperty("vendorId", out var vendorElement) &&
            root.TryGetProperty("productId", out var productElement) &&
            vendorElement.TryGetInt32(out vendorId) &&
            productElement.TryGetInt32(out productId);

        // 0x045E is Microsoft. The id table is checked first because it is
        // more specific than anything Windows reports for these pads.
        if (hasIds && vendorId == 0x045E && KnownXboxProducts.TryGetValue(productId, out var known))
        {
            return known;
        }

        if (!string.IsNullOrWhiteSpace(reported) && !IsGenericName(reported))
        {
            return reported;
        }

        if (hasIds && (vendorId != 0 || productId != 0))
        {
            // Unknown hardware: the ids are still more actionable than a
            // placeholder, and they are what a bug report needs.
            return string.IsNullOrWhiteSpace(reported)
                ? $"Controller {vendorId:X4}:{productId:X4}"
                : $"{reported} ({vendorId:X4}:{productId:X4})";
        }

        return string.IsNullOrWhiteSpace(reported) ? GenericName : reported;
    }

    private static bool IsGenericName(string name) =>
        GenericNameMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadControl(JsonElement root, out ControllerControl control)
    {
        control = ControllerControl.None;
        return root.TryGetProperty("control", out var controlElement) &&
            Enum.TryParse(controlElement.GetString(), ignoreCase: false, out control);
    }

    private static bool ReadBoolean(JsonElement root, string property, bool fallback) =>
        root.TryGetProperty(property, out var element) &&
        element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : fallback;

    private static async Task AwaitLoopAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }
}
