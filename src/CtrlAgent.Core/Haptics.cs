namespace CtrlAgent.Core;

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
/// The built-in cues.
/// <para>
/// <b>Every number in this file is an estimate.</b> Not one of these patterns
/// has been felt on a real controller — the amplitudes, the frame lengths, and
/// the gaps between pulses were all chosen by reading, not by holding a pad.
/// They are plausible, they are deliberately short and structurally distinct
/// from one another, and that is the entire basis for them.
/// </para>
/// <para>
/// This matters more than it looks. The cues <em>are</em> the product: the
/// premise of this tool is that you can keep your eyes off the screen because
/// your hands are told what happened. A cue that is too weak to notice, too
/// similar to its neighbour, or unpleasant to receive fifty times an hour
/// defeats that premise while every test still passes — none of these
/// qualities is visible from code, and none is covered by the harness.
/// </para>
/// <para>
/// Treat these as placeholders until the tuning pass runs against real
/// hardware (roadmap #7, gated on the validation runs in #1). Motors also
/// differ per device: Xbox low/high-frequency pairs, DualSense voice-coil
/// actuators, and the Elite trigger motors will not feel alike at the same
/// numbers, so "tuned" ultimately means tuned per transport.
/// </para>
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
