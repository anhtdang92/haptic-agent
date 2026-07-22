namespace HapticAgent.Core;

/// <summary>
/// One timed controller-rumble frame. Intensities are normalized to 0..1.
/// </summary>
public readonly record struct RumbleFrame(
    float LowFrequency,
    float HighFrequency,
    float LeftTrigger,
    float RightTrigger,
    TimeSpan Duration)
{
    public static RumbleFrame Create(
        float lowFrequency,
        float highFrequency,
        float leftTrigger,
        float rightTrigger,
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "A haptic frame must have a positive duration.");
        }

        return new RumbleFrame(
            Math.Clamp(lowFrequency, 0f, 1f),
            Math.Clamp(highFrequency, 0f, 1f),
            Math.Clamp(leftTrigger, 0f, 1f),
            Math.Clamp(rightTrigger, 0f, 1f),
            duration);
    }

    public static RumbleFrame Silence(TimeSpan duration) =>
        Create(0f, 0f, 0f, 0f, duration);
}

public sealed record HapticPattern(
    string Name,
    IReadOnlyList<RumbleFrame> Frames,
    bool Loop = false)
{
    public TimeSpan Duration =>
        TimeSpan.FromTicks(Frames.Sum(frame => frame.Duration.Ticks));
}

/// <summary>
/// Built-in cues are intentionally short and distinct. Real-device validation
/// will tune these values before they become stable defaults.
/// </summary>
public static class HapticPatternCatalog
{
    public static HapticPattern Working { get; } = new(
        "working",
        [
            RumbleFrame.Create(0.20f, 0.05f, 0f, 0f, TimeSpan.FromMilliseconds(90)),
        ]);

    public static HapticPattern ApprovalRequired { get; } = new(
        "approval-required",
        [
            RumbleFrame.Create(0.15f, 0.65f, 0.35f, 0f, TimeSpan.FromMilliseconds(110)),
            RumbleFrame.Silence(TimeSpan.FromMilliseconds(90)),
            RumbleFrame.Create(0.15f, 0.65f, 0f, 0.35f, TimeSpan.FromMilliseconds(110)),
        ],
        Loop: true);

    public static HapticPattern WaitingForInput { get; } = new(
        "waiting-for-input",
        [
            RumbleFrame.Create(0.10f, 0.35f, 0f, 0f, TimeSpan.FromMilliseconds(80)),
            RumbleFrame.Silence(TimeSpan.FromMilliseconds(70)),
            RumbleFrame.Create(0.10f, 0.35f, 0f, 0f, TimeSpan.FromMilliseconds(80)),
        ]);

    public static HapticPattern Completed { get; } = new(
        "completed",
        [
            RumbleFrame.Create(0.35f, 0.20f, 0f, 0f, TimeSpan.FromMilliseconds(85)),
            RumbleFrame.Silence(TimeSpan.FromMilliseconds(65)),
            RumbleFrame.Create(0.35f, 0.20f, 0f, 0f, TimeSpan.FromMilliseconds(85)),
        ]);

    public static HapticPattern Error { get; } = new(
        "error",
        [
            RumbleFrame.Create(0.90f, 0.45f, 0.25f, 0.25f, TimeSpan.FromMilliseconds(450)),
        ]);
}

public sealed class FeedbackRouter
{
    public HapticPattern? Route(AgentEvent agentEvent) =>
        agentEvent.State switch
        {
            AgentStateKind.Working => HapticPatternCatalog.Working,
            AgentStateKind.ApprovalRequired => HapticPatternCatalog.ApprovalRequired,
            AgentStateKind.WaitingForInput => HapticPatternCatalog.WaitingForInput,
            AgentStateKind.Completed => HapticPatternCatalog.Completed,
            AgentStateKind.Error => HapticPatternCatalog.Error,
            _ => null,
        };
}
