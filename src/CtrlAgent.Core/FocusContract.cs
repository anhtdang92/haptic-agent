namespace CtrlAgent.Core;

public enum FocusMode
{
    DeepFocus,
    ActiveSupervision,
    SilentWatch,
    Couch,
    Accessibility,
}

public enum AttentionEventKind
{
    Navigation,
    CommandAcknowledgement,
    RoutineProgress,
    ToolActivity,
    WaitingForInput,
    ApprovalRequired,
    Completed,
    Interrupted,
    Error,
    Voice,
    System,
}

public sealed record FocusContract(
    FocusMode Mode,
    bool NotifyNavigation,
    bool NotifyCommands,
    bool NotifyProgress,
    bool NotifyToolActivity,
    bool NotifyWaitingForInput,
    bool NotifyApprovals,
    bool NotifyCompletion,
    bool NotifyInterruptions,
    bool NotifyErrors,
    bool NotifyVoice,
    bool NotifySystem,
    TimeSpan StalledWorkThreshold,
    float IntensityMultiplier)
{
    public static FocusContract For(FocusMode mode) => mode switch
    {
        FocusMode.DeepFocus => new(
            mode, false, true, false, false, true, true, true, true, true, true, true,
            TimeSpan.FromMinutes(5), 0.85f),
        FocusMode.SilentWatch => new(
            mode, false, false, false, false, false, true, false, true, true, false, false,
            TimeSpan.FromMinutes(10), 0.70f),
        FocusMode.Couch => new(
            mode, true, true, true, true, true, true, true, true, true, true, true,
            TimeSpan.FromMinutes(3), 1.00f),
        FocusMode.Accessibility => new(
            mode, true, true, true, true, true, true, true, true, true, true, true,
            TimeSpan.FromMinutes(4), 0.90f),
        _ => new(
            FocusMode.ActiveSupervision, true, true, true, true, true, true, true, true, true, true, true,
            TimeSpan.FromMinutes(5), 0.85f),
    };

    public bool Allows(AttentionEventKind kind) => kind switch
    {
        AttentionEventKind.Navigation => NotifyNavigation,
        AttentionEventKind.CommandAcknowledgement => NotifyCommands,
        AttentionEventKind.RoutineProgress => NotifyProgress,
        AttentionEventKind.ToolActivity => NotifyToolActivity,
        AttentionEventKind.WaitingForInput => NotifyWaitingForInput,
        AttentionEventKind.ApprovalRequired => NotifyApprovals,
        AttentionEventKind.Completed => NotifyCompletion,
        AttentionEventKind.Interrupted => NotifyInterruptions,
        AttentionEventKind.Error => NotifyErrors,
        AttentionEventKind.Voice => NotifyVoice,
        AttentionEventKind.System => NotifySystem,
        _ => false,
    };
}

/// <summary>Process-wide focus policy shared by all hosts and UI surfaces.</summary>
public static class FocusContractSettings
{
    private static readonly FocusMode[] Cycle =
    [
        FocusMode.DeepFocus,
        FocusMode.ActiveSupervision,
        FocusMode.SilentWatch,
        FocusMode.Couch,
        FocusMode.Accessibility,
    ];

    private static readonly object Sync = new();
    private static FocusContract _current = FocusContract.For(FocusMode.ActiveSupervision);

    public static event Action<FocusContract>? Changed;

    public static FocusContract Current
    {
        get
        {
            lock (Sync)
            {
                return _current;
            }
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Action<FocusContract>? changed;
            lock (Sync)
            {
                if (_current == value)
                {
                    return;
                }
                _current = value;
                changed = Changed;
            }
            changed?.Invoke(value);
        }
    }

    public static IReadOnlyList<FocusMode> Modes => Cycle;

    public static void Select(FocusMode mode) => Current = FocusContract.For(mode);

    public static FocusMode Next()
    {
        var current = Current.Mode;
        var index = Array.IndexOf(Cycle, current);
        var next = Cycle[(index + 1 + Cycle.Length) % Cycle.Length];
        Select(next);
        return next;
    }

    public static string Label(FocusMode mode) => mode switch
    {
        FocusMode.DeepFocus => "Deep Focus",
        FocusMode.ActiveSupervision => "Active Supervision",
        FocusMode.SilentWatch => "Silent Watch",
        FocusMode.Couch => "Couch",
        FocusMode.Accessibility => "Accessibility",
        _ => mode.ToString(),
    };

    public static string Description(FocusMode mode) => mode switch
    {
        FocusMode.DeepFocus => "Routine progress stays quiet; approvals, completion, interruption, and errors reach you.",
        FocusMode.ActiveSupervision => "Full tactile supervision for commands, progress, decisions, and results.",
        FocusMode.SilentWatch => "Only approvals, interruptions, and failures request attention.",
        FocusMode.Couch => "Comprehensive, stronger feedback for operation away from the screen.",
        FocusMode.Accessibility => "All semantic cues remain available for explicit multimodal operation.",
        _ => string.Empty,
    };
}

public sealed record AttentionMetricsSnapshot(
    long HapticNotificationsDelivered,
    long RoutineNotificationsSuppressed,
    long ApprovalRequestsSurfaced,
    long ApprovalResponsesHandled,
    long CompletionsSurfaced,
    long ErrorsSurfaced,
    TimeSpan AutonomousWorkObserved)
{
    public long AvoidedRoutineInterruptions => RoutineNotificationsSuppressed;
    public long DecisionsHandled => ApprovalResponsesHandled;
}

public static class AttentionMetricsRegistry
{
    public static AttentionMetrics Current { get; } = new();
}

public sealed class AttentionMetrics
{
    private readonly object _sync = new();
    private long _delivered;
    private long _suppressed;
    private long _approvalRequests;
    private long _approvalResponses;
    private long _completions;
    private long _errors;
    private DateTimeOffset? _workingSince;
    private TimeSpan _autonomousWork;

    public event Action? Changed;

    public void RecordDecision(AttentionEventKind kind, bool delivered)
    {
        lock (_sync)
        {
            if (delivered)
            {
                _delivered++;
                if (kind == AttentionEventKind.ApprovalRequired) _approvalRequests++;
                if (kind == AttentionEventKind.Completed) _completions++;
                if (kind == AttentionEventKind.Error) _errors++;
            }
            else if (kind is AttentionEventKind.Navigation or AttentionEventKind.CommandAcknowledgement or
                     AttentionEventKind.RoutineProgress or AttentionEventKind.ToolActivity or AttentionEventKind.System)
            {
                _suppressed++;
            }
        }
        Changed?.Invoke();
    }

    public void RecordApprovalResponse()
    {
        lock (_sync)
        {
            _approvalResponses++;
        }
        Changed?.Invoke();
    }

    public void ObserveAgentState(AgentStateKind state, DateTimeOffset observedAt)
    {
        var changed = false;
        lock (_sync)
        {
            if (state == AgentStateKind.Working)
            {
                if (_workingSince is null)
                {
                    _workingSince = observedAt;
                    changed = true;
                }
            }
            else if (_workingSince is { } started)
            {
                _autonomousWork += observedAt - started;
                _workingSince = null;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    public AttentionMetricsSnapshot Snapshot(DateTimeOffset? now = null)
    {
        lock (_sync)
        {
            var observed = _autonomousWork;
            if (_workingSince is { } started)
            {
                observed += (now ?? DateTimeOffset.UtcNow) - started;
            }

            return new(
                _delivered,
                _suppressed,
                _approvalRequests,
                _approvalResponses,
                _completions,
                _errors,
                observed);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _delivered = 0;
            _suppressed = 0;
            _approvalRequests = 0;
            _approvalResponses = 0;
            _completions = 0;
            _errors = 0;
            _workingSince = null;
            _autonomousWork = TimeSpan.Zero;
        }
        Changed?.Invoke();
    }
}
