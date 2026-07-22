namespace HapticAgent.Core;

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

    public async ValueTask PlayAsync(
        HapticPattern pattern,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pattern);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelActivePattern();
            _activePattern = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeTask = RunPatternAsync(pattern, _activePattern.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Task? previous;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            previous = _activeTask;
            CancelActivePattern();
        }
        finally
        {
            _gate.Release();
        }

        await AwaitPatternAsync(previous).ConfigureAwait(false);
        await _controller.StopHapticsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var previous = _activeTask;
        CancelActivePattern();
        await AwaitPatternAsync(previous).ConfigureAwait(false);

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

    private async Task RunPatternAsync(HapticPattern pattern, CancellationToken cancellationToken)
    {
        try
        {
            await _controller.PlayAsync(pattern, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Replaced by a newer feedback cue or explicitly stopped.
        }
    }

    private void CancelActivePattern()
    {
        _activePattern?.Cancel();
        _activePattern?.Dispose();
        _activePattern = null;
        _activeTask = null;
    }

    private static async Task AwaitPatternAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
