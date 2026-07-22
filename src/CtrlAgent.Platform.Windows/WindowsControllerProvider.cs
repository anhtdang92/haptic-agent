using CtrlAgent.Core;

namespace CtrlAgent.Platform.Windows;

public sealed class WindowsControllerProvider : IControllerProvider
{
    private readonly string? _explicitBridgePath;
    private readonly XInputControllerProvider _xInput = new();
    private GameInputBridgeControllerDevice? _gameInput;
    private bool _bridgeAttempted;
    private bool _disposed;

    public WindowsControllerProvider(string? gameInputBridgePath = null)
    {
        _explicitBridgePath = gameInputBridgePath;
    }

    public async ValueTask<IControllerDevice?> GetPrimaryControllerAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A dead bridge (crashed process or disposed device) must not be
        // handed out again; drop it and allow one fresh bridge attempt.
        if (_gameInput is { IsDefunct: true })
        {
            await _gameInput.DisposeAsync().ConfigureAwait(false);
            _gameInput = null;
            _bridgeAttempted = false;
        }

        if (!_bridgeAttempted)
        {
            _bridgeAttempted = true;
            var bridgePath = ResolveBridgePath(_explicitBridgePath);
            if (bridgePath is not null)
            {
                try
                {
                    _gameInput = await GameInputBridgeControllerDevice.StartAsync(
                        bridgePath,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidOperationException or TimeoutException)
                {
                    _gameInput = null;
                }
            }
        }

        if (_gameInput is not null)
        {
            return _gameInput;
        }

        return await _xInput.GetPrimaryControllerAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_gameInput is not null)
        {
            await _gameInput.DisposeAsync().ConfigureAwait(false);
        }

        await _xInput.DisposeAsync().ConfigureAwait(false);
    }

    private static string? ResolveBridgePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var environmentPath = Environment.GetEnvironmentVariable("CTRL_AGENT_GAMEINPUT_BRIDGE");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        var adjacent = Path.Combine(AppContext.BaseDirectory, "CtrlAgent.GameInputBridge.exe");
        return File.Exists(adjacent) ? adjacent : null;
    }
}
