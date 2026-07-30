namespace CtrlAgent.Core;

/// <summary>One timed controller-rumble frame. Intensities are normalized to 0..1.</summary>
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
            throw new ArgumentOutOfRangeException(nameof(duration), "A haptic frame must have a positive duration.");
        }

        return new RumbleFrame(
            Math.Clamp(lowFrequency, 0f, 1f),
            Math.Clamp(highFrequency, 0f, 1f),
            Math.Clamp(leftTrigger, 0f, 1f),
            Math.Clamp(rightTrigger, 0f, 1f),
            duration);
    }

    public static RumbleFrame Silence(TimeSpan duration) => Create(0f, 0f, 0f, 0f, duration);

    public RumbleFrame Scale(float intensity, ControllerCapabilities capabilities)
    {
        intensity = Math.Clamp(intensity, 0f, 1f);
        return Create(
            capabilities.HasLowFrequencyRumble ? LowFrequency * intensity : 0f,
            capabilities.HasHighFrequencyRumble ? HighFrequency * intensity : 0f,
            capabilities.HasLeftTriggerRumble ? LeftTrigger * intensity : 0f,
            capabilities.HasRightTriggerRumble ? RightTrigger * intensity : 0f,
            Duration);
    }
}

public enum HapticCategory
{
    System,
    Navigation,
    Command,
    Progress,
    Approval,
    Success,
    Warning,
    Error,
    Voice,
}

public sealed record HapticPattern(
    string Name,
    IReadOnlyList<RumbleFrame> Frames,
    bool Loop = false,
    HapticCategory Category = HapticCategory.System,
    int Priority = 50)
{
    public TimeSpan Duration => TimeSpan.FromTicks(Frames.Sum(frame => frame.Duration.Ticks));

    public HapticPattern Adapt(float intensity, ControllerCapabilities capabilities) =>
        this with { Frames = [.. Frames.Select(frame => frame.Scale(intensity, capabilities))] };
}

/// <summary>
/// App-wide haptic preferences. These are intentionally transport-neutral;
/// device capability adaptation happens immediately before playback.
/// </summary>
public static class HapticSettings
{
    public static bool Enabled { get; set; } = true;
    public static float MasterIntensity { get; set; } = 0.75f;
    public static bool NavigationEnabled { get; set; } = true;
    public static bool ProgressEnabled { get; set; } = true;
    public static bool ApprovalRemindersEnabled { get; set; } = true;

    public static bool Allows(HapticPattern pattern) =>
        Enabled &&
        (pattern.Category != HapticCategory.Navigation || NavigationEnabled) &&
        (pattern.Category != HapticCategory.Progress || ProgressEnabled) &&
        (pattern.Category != HapticCategory.Approval || ApprovalRemindersEnabled);
}

/// <summary>
/// CtrlAgent's tactile language. Patterns are designed as recognizable shapes,
/// not arbitrary vibration: rising means progress, falling means cancellation,
/// alternating sides means a decision, and a heavy sustained pulse means failure.
/// </summary>
public static class HapticPatternCatalog
{
    private static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);
    private static RumbleFrame F(float low, float high, int ms, float left = 0f, float right = 0f) =>
        RumbleFrame.Create(low, high, left, right, Ms(ms));
    private static RumbleFrame Gap(int ms) => RumbleFrame.Silence(Ms(ms));

    public static HapticPattern Connected { get; } = new("connected", [
        F(.10f, .18f, 45), Gap(35), F(.18f, .30f, 55), Gap(35), F(.28f, .45f, 70)
    ], Category: HapticCategory.System, Priority: 70);

    public static HapticPattern Disconnected { get; } = new("disconnected", [
        F(.35f, .30f, 80), Gap(45), F(.22f, .16f, 70), Gap(45), F(.12f, .08f, 65)
    ], Category: HapticCategory.Warning, Priority: 85);

    public static HapticPattern NavigationTick { get; } = new("navigation-tick", [
        F(.02f, .17f, 28)
    ], Category: HapticCategory.Navigation, Priority: 10);

    public static HapticPattern Boundary { get; } = new("navigation-boundary", [
        F(.18f, .10f, 45), Gap(30), F(.18f, .10f, 45)
    ], Category: HapticCategory.Navigation, Priority: 25);

    public static HapticPattern CommandAccepted { get; } = new("command-accepted", [
        F(.08f, .28f, 45), Gap(25), F(.16f, .40f, 60)
    ], Category: HapticCategory.Command, Priority: 45);

    public static HapticPattern CommandRejected { get; } = new("command-rejected", [
        F(.45f, .12f, 85), Gap(45), F(.45f, .12f, 85)
    ], Category: HapticCategory.Warning, Priority: 80);

    public static HapticPattern PromptQueued { get; } = new("prompt-queued", [
        F(.08f, .24f, 45), Gap(45), F(.08f, .24f, 45), Gap(45), F(.08f, .24f, 45)
    ], Category: HapticCategory.Command, Priority: 45);

    public static HapticPattern QueueFull { get; } = new("queue-full", [
        F(.62f, .20f, 120), Gap(45), F(.62f, .20f, 120), Gap(45), F(.62f, .20f, 120)
    ], Category: HapticCategory.Error, Priority: 95);

    public static HapticPattern Working { get; } = new("working", [
        F(.13f, .05f, 70), Gap(420)
    ], Loop: true, Category: HapticCategory.Progress, Priority: 20);

    public static HapticPattern Thinking { get; } = new("thinking", [
        F(.04f, .14f, 45), Gap(120), F(.04f, .20f, 45), Gap(120), F(.04f, .26f, 45), Gap(520)
    ], Loop: true, Category: HapticCategory.Progress, Priority: 22);

    public static HapticPattern ToolStarted { get; } = new("tool-started", [
        F(.18f, .06f, 55, left: .20f), Gap(30), F(.18f, .06f, 55, right: .20f)
    ], Category: HapticCategory.Progress, Priority: 35);

    public static HapticPattern ToolFinished { get; } = new("tool-finished", [
        F(.08f, .24f, 45, right: .12f), Gap(30), F(.16f, .34f, 60, right: .20f)
    ], Category: HapticCategory.Success, Priority: 55);

    public static HapticPattern ApprovalRequired { get; } = new("approval-required", [
        F(.14f, .62f, 105, left: .38f), Gap(85), F(.14f, .62f, 105, right: .38f), Gap(900)
    ], Loop: true, Category: HapticCategory.Approval, Priority: 90);

    public static HapticPattern ApprovedOnce { get; } = new("approved-once", [
        F(.08f, .30f, 50, right: .18f), Gap(25), F(.18f, .48f, 75, right: .32f)
    ], Category: HapticCategory.Success, Priority: 92);

    public static HapticPattern ApprovedSession { get; } = new("approved-session", [
        F(.08f, .28f, 45, right: .12f), Gap(25), F(.15f, .42f, 60, right: .24f), Gap(25),
        F(.24f, .58f, 85, right: .38f)
    ], Category: HapticCategory.Success, Priority: 93);

    public static HapticPattern Declined { get; } = new("declined", [
        F(.55f, .10f, 105, left: .35f), Gap(55), F(.38f, .08f, 85, left: .25f)
    ], Category: HapticCategory.Warning, Priority: 93);

    public static HapticPattern WaitingForInput { get; } = new("waiting-for-input", [
        F(.08f, .34f, 70), Gap(65), F(.08f, .34f, 70), Gap(650)
    ], Loop: true, Category: HapticCategory.Approval, Priority: 82);

    public static HapticPattern VoiceListening { get; } = new("voice-listening", [
        F(.02f, .16f, 35), Gap(465)
    ], Loop: true, Category: HapticCategory.Voice, Priority: 60);

    public static HapticPattern VoiceRecognized { get; } = new("voice-recognized", [
        F(.06f, .24f, 45), Gap(25), F(.12f, .42f, 75)
    ], Category: HapticCategory.Voice, Priority: 72);

    public static HapticPattern VoiceFailed { get; } = new("voice-failed", [
        F(.42f, .12f, 95), Gap(50), F(.24f, .08f, 75)
    ], Category: HapticCategory.Warning, Priority: 82);

    public static HapticPattern Interrupted { get; } = new("interrupted", [
        F(.42f, .22f, 70), Gap(35), F(.24f, .12f, 60), Gap(35), F(.10f, .05f, 50)
    ], Category: HapticCategory.Warning, Priority: 88);

    public static HapticPattern Completed { get; } = new("completed", [
        F(.12f, .26f, 55), Gap(35), F(.20f, .42f, 70), Gap(35), F(.30f, .58f, 90)
    ], Category: HapticCategory.Success, Priority: 75);

    public static HapticPattern Error { get; } = new("error", [
        F(.88f, .42f, 240, left: .28f, right: .28f), Gap(80),
        F(.88f, .42f, 240, left: .28f, right: .28f)
    ], Category: HapticCategory.Error, Priority: 100);
}

/// <summary>Maps agent and command semantics onto the tactile language.</summary>
public sealed class FeedbackRouter
{
    public HapticPattern? Route(AgentEvent agentEvent)
    {
        var message = agentEvent.Message ?? string.Empty;
        return agentEvent.State switch
        {
            AgentStateKind.Working when Contains(message, "thinking") => HapticPatternCatalog.Thinking,
            AgentStateKind.Working when ContainsAny(message, "tool", "reading", "searching", "running", "executing") =>
                HapticPatternCatalog.ToolStarted,
            AgentStateKind.Working => HapticPatternCatalog.Working,
            AgentStateKind.ApprovalRequired => HapticPatternCatalog.ApprovalRequired,
            AgentStateKind.WaitingForInput => HapticPatternCatalog.WaitingForInput,
            AgentStateKind.Completed when ContainsAny(message, "interrupt", "cancel") => HapticPatternCatalog.Interrupted,
            AgentStateKind.Completed => HapticPatternCatalog.Completed,
            AgentStateKind.Error => HapticPatternCatalog.Error,
            _ => null,
        };
    }

    public HapticPattern? Route(AgentCommandKind command) => command switch
    {
        AgentCommandKind.ApproveOnce => HapticPatternCatalog.ApprovedOnce,
        AgentCommandKind.ApproveForSession => HapticPatternCatalog.ApprovedSession,
        AgentCommandKind.Decline => HapticPatternCatalog.Declined,
        AgentCommandKind.Cancel or AgentCommandKind.Interrupt => HapticPatternCatalog.Interrupted,
        AgentCommandKind.StartVoicePrompt => HapticPatternCatalog.VoiceListening,
        AgentCommandKind.ScrollOutputUp or AgentCommandKind.ScrollOutputDown => HapticPatternCatalog.NavigationTick,
        AgentCommandKind.SubmitPrompt or AgentCommandKind.NewSession or AgentCommandKind.ReviewChanges or
        AgentCommandKind.SetModel or AgentCommandKind.SetEffort or AgentCommandKind.SetPermissionMode or
        AgentCommandKind.CompactContext or AgentCommandKind.AttachFile or AgentCommandKind.ResumeSession or
        AgentCommandKind.NextSession or AgentCommandKind.PreviousSession => HapticPatternCatalog.CommandAccepted,
        _ => null,
    };

    private static bool Contains(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => Contains(value, token));
}
