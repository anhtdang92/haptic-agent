namespace CtrlAgent.Core;

/// <summary>
/// Owns the currently active haptic cue. Starting a new cue cancels and drains
/// the previous one before the replacement begins, while returning control to
/// the caller immediately after the new cue has been scheduled.
/// </summary>
public sealed class HapticScheduler : IAsyncDisposable
{
    private readonly IControllerDevice _controller;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activePattern;
    private Task? _activeTask;
    private bool _disposed;

    public HapticScheduler(IControllerDevice controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    /// <summary>
    /// Schedules a pattern without waiting for the entire pattern to finish.
    /// This keeps the agent event loop responsive while a repeating cue plays.
    /// </summary>
    public async ValueTask PlayAsync(
        HapticPattern pattern,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pattern);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CancelActivePatternAsync().ConfigureAwait(false);

            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activePattern = linked;
            _activeTask = RunPatternAsync(pattern, linked.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CancelActivePatternAsync().ConfigureAwait(false);
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

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await CancelActivePatternAsync().ConfigureAwait(false);

            try
            {
                await _controller.StopHapticsAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Best-effort shutdown when a controller or bridge disappeared.
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task CancelActivePatternAsync()
    {
        var cancellation = _activePattern;
        var task = _activeTask;
        _activePattern = null;
        _activeTask = null;

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task RunPatternAsync(HapticPattern pattern, CancellationToken cancellationToken)
    {
        try
        {
            await _controller.PlayAsync(pattern, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when a newer cue replaces this one or haptics are stopped.
        }
    }
}
