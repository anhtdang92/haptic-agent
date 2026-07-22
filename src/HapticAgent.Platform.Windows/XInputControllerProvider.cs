using HapticAgent.Core;

namespace HapticAgent.Platform.Windows;

public sealed class XInputControllerProvider : IControllerProvider
{
    private bool _disposed;

    public ValueTask<IControllerDevice?> GetPrimaryControllerAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        for (uint index = 0; index < 4; index++)
        {
            if (XInputNative.GetState(index, out _) == XInputNative.ErrorSuccess)
            {
                return ValueTask.FromResult<IControllerDevice?>(new XInputControllerDevice(index));
            }
        }

        return ValueTask.FromResult<IControllerDevice?>(null);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
