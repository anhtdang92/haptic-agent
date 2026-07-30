namespace CtrlAgent.Core;

/// <summary>
/// The amount of attention CtrlAgent may request from the user. A focus mode is
/// not a cosmetic preset: it is the contract between autonomous agent work and
/// the person supervising it.
/// </summary>
public enum FocusMode
{
    DeepFocus,
    ActiveSupervision,
    SilentWatch,
    Couch,
    Accessibility,
}

/// <summary>Semantic events evaluated by a <see cref="FocusContract"/>.</summary>
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

/// <summary>
/// Defines exactly which events are allowed to interrupt the user's focus.
/// Critical safety events are deliberately represented separately from routine
/// progress so a quiet mode can suppress noise without hiding approvals or
/// failures.
/// </summary>
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
            mode,
            NotifyNavigation: false,
            NotifyCommands: true,
            NotifyProgress: false,
            NotifyToolActivity: false,
            NotifyWaitingForInput: true,
            NotifyApprovals: true,
            NotifyCompletion: true,
            NotifyInterruptions: true,
            NotifyErrors: true,
            NotifyVoice: true,
            NotifySystem: true,
            StalledWorkThreshold: TimeSpan.FromMinutes(5),
            IntensityMultiplier: 0.85f),

        FocusMode.SilentWatch => new(
            mode,
            NotifyNavigation: false,
            NotifyCommands: false,
            NotifyProgress: false,
            NotifyToolActivity: false,
            NotifyWaitingForInput: false,
            NotifyApprovals: true,
            NotifyCompletion: false,
            NotifyInterruptions: true,
            NotifyErrors: true,
            NotifyVoice: false,
            NotifySystem: false,
            StalledWorkThreshold: TimeSpan.FromMinutes(10),
            IntensityMultiplier: 0.70f),

        FocusMode.Couch => new(
            mode,
            NotifyNavigation: true,
            NotifyCommands: true,
            NotifyProgress: true,
            NotifyToolActivity: true,
            NotifyWaitingForInput: true,
            NotifyApprovals: true,
            NotifyCompletion: true,
            NotifyInterruptions: true,
            NotifyErrors: true,
            NotifyVoice: true,
            NotifySystem: true,
            StalledWorkThreshold: TimeSpan.FromMinutes(3),
            IntensityMultiplier: 1.00f),

        FocusMode.Accessibility => new(
            mode,
            NotifyNavigation: true,
            NotifyCommands: true,
            NotifyProgress: true,
            NotifyToolActivity: true,
            NotifyWaitingForInput: true,
            NotifyApprovals: true,
            NotifyCompletion: true,
            NotifyInterruptions: true,
            NotifyErrors: true,
            NotifyVoice: true,
            NotifySystem: true,
            StalledWorkThreshold: TimeSpan.FromMinutes(4),
            IntensityMultiplier: 0.90f),

        _ => new(
            FocusMode.ActiveSupervision,
            NotifyNavigation: true,
            NotifyCommands: true,
            NotifyProgress: true,
            NotifyToolActivity: true,
            NotifyWaitingForInput: true,
            NotifyApprovals: true,
            NotifyCompletion: true,
            NotifyInterruptions: true,
            NotifyErrors: true,
            NotifyVoice: true,
            NotifySystem: true,
            StalledWorkThreshold: TimeSpan.FromMinutes(5),
            IntensityMultiplier: 0.85f),
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

/// <summary>Process-wide focus policy shared by all host surfaces.</summary>
public static class FocusContractSettings
{
    private static FocusContract _current = FocusContract.For(FocusMode.ActiveSupervision);

    public static FocusContract Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static void Select(FocusMode mode) => Current = FocusContract.For(mode);
}

/// <summary>
/// Privacy-preserving counters that describe attention saved without recording
/// prompt text, filenames, tool arguments, controller identity, or agent output.
/// </summary>
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
    }

    public void RecordApprovalResponse()
    {
        lock (_sync)
        {
            _approvalResponses++;
        }
    }

    public void ObserveAgentState(AgentStateKind state, DateTimeOffset observedAt)
    {
        lock (_sync)
        {
            if (state == AgentStateKind.Working)
            {
                _workingSince ??= observedAt;
                return;
            }

            if (_workingSince is { } started)
            {
                _autonomousWork += observedAt - started;
                _workingSince = null;
            }
        }
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
}
