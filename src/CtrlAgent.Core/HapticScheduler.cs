namespace CtrlAgent.Core;

/// <summary>
/// Owns two semantic haptic layers: one persistent state (working, approval,
/// waiting, voice listening) and one transient notification. A transient cue
/// temporarily interrupts the persistent state and automatically resumes it
/// when finished. This prevents a navigation tick or command acknowledgement
/// from permanently silencing an approval reminder.
/// </summary>
public sealed class HapticScheduler : IAsyncDisposable
{
    private readonly IControllerDevice _controller;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activePlayback;
    private Task? _activeTask;
    private HapticPattern? _persistentPattern;
    private long _generation;
    private bool _disposed;

    public HapticScheduler(IControllerDevice controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    /// <summary>
    /// Looping patterns become the persistent layer. Non-looping patterns play
    /// as transients and then resume the latest persistent layer. The caller is
    /// released after playback has been scheduled, not after the cue completes.
    /// </summary>
    public async ValueTask PlayAsync(
        HapticPattern pattern,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pattern);

        var adapted = AdaptOrNull(pattern);
        if (adapted is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = ++_generation;
            if (adapted.Loop)
            {
                _persistentPattern = adapted;
                await CancelActivePlaybackAsync().ConfigureAwait(false);
                StartPlayback(adapted, generation, cancellationToken);
                return;
            }

            await CancelActivePlaybackAsync().ConfigureAwait(false);
            StartTransient(adapted, generation, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears only the persistent state and stops all physical output.</summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ++_generation;
            _persistentPattern = null;
            await CancelActivePlaybackAsync().ConfigureAwait(false);
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
            ++_generation;
            _persistentPattern = null;
            await CancelActivePlaybackAsync().ConfigureAwait(false);

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

    private HapticPattern? AdaptOrNull(HapticPattern pattern)
    {
        if (!HapticSettings.Allows(pattern))
        {
            return null;
        }

        var adapted = pattern.Adapt(HapticSettings.EffectiveIntensity, _controller.Capabilities);
        return adapted.Frames.All(frame =>
            frame.LowFrequency == 0f && frame.HighFrequency == 0f &&
            frame.LeftTrigger == 0f && frame.RightTrigger == 0f)
            ? null
            : adapted;
    }

    private void StartPlayback(
        HapticPattern pattern,
        long generation,
        CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activePlayback = linked;
        _activeTask = RunPatternAsync(pattern, generation, linked.Token);
    }

    private void StartTransient(
        HapticPattern transient,
        long generation,
        CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activePlayback = linked;
        _activeTask = RunTransientAndResumeAsync(transient, generation, linked.Token);
    }

    private async Task RunTransientAndResumeAsync(
        HapticPattern transient,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _controller.PlayAsync(transient, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            HapticPattern? persistent;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || generation != _generation)
                {
                    return;
                }

                persistent = _persistentPattern;
                if (persistent is null)
                {
                    _activePlayback = null;
                    _activeTask = null;
                    return;
                }

                var resumed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activePlayback = resumed;
                _activeTask = RunPatternAsync(persistent, generation, resumed.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CancelActivePlaybackAsync()
    {
        var cancellation = _activePlayback;
        var task = _activeTask;
        _activePlayback = null;
        _activeTask = null;

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (task is not null && task.Id != Task.CurrentId)
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

    private async Task RunPatternAsync(
        HapticPattern pattern,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _controller.PlayAsync(pattern, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == _generation && !pattern.Loop)
            {
                try
                {
                    await _controller.StopHapticsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
    }
}

/// <summary>
/// Routes haptic playback to the currently attached scheduler so consumers
/// survive controller loss and reconnection. Calls while no scheduler is
/// attached are silent no-ops, and device-loss failures are swallowed instead
/// of tearing down the caller.
/// </summary>
public sealed class HapticSchedulerHub
{
    private readonly object _sync = new();
    private HapticScheduler? _current;

    public void Attach(HapticScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        lock (_sync)
        {
            _current = scheduler;
        }
    }

    public void Detach(HapticScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        lock (_sync)
        {
            if (ReferenceEquals(_current, scheduler))
            {
                _current = null;
            }
        }
    }

    public async ValueTask PlayAsync(HapticPattern pattern, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var scheduler = Current();
        if (scheduler is null)
        {
            return;
        }

        try
        {
            await scheduler.PlayAsync(pattern, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsDeviceLoss(exception))
        {
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var scheduler = Current();
        if (scheduler is null)
        {
            return;
        }

        try
        {
            await scheduler.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsDeviceLoss(exception))
        {
        }
    }

    private HapticScheduler? Current()
    {
        lock (_sync)
        {
            return _current;
        }
    }

    private static bool IsDeviceLoss(Exception exception) =>
        exception is ObjectDisposedException or IOException or InvalidOperationException;
}
