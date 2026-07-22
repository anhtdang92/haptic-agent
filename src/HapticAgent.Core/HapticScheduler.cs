namespace HapticAgent.Core;

public sealed class HapticScheduler : IAsyncDisposable
{
    private readonly IControllerDevice _controller;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activePattern;
    private bool _disposed;

    public HapticScheduler(IControllerDevice controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public async ValueTask PlayAsync(
        HapticPattern pattern,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancellationTokenSource linked;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _activePattern?.Cancel();
            _activePattern?.Dispose();
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activePattern = linked;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await _controller.PlayAsync(pattern, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Replaced by a newer feedback cue or explicitly stopped.
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _activePattern?.Cancel();
            _activePattern?.Dispose();
            _activePattern = null;
            await _controller.StopHapticsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activePattern?.Cancel();
        _activePattern?.Dispose();
        _activePattern = null;

        try
        {
            await _controller.StopHapticsAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort shutdown: never leave disposal blocked by a removed device.
        }

        _gate.Dispose();
    }
}
