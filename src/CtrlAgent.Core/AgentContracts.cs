namespace CtrlAgent.Core;

public enum AgentStateKind
{
    Unknown = 0,
    Idle,
    Working,
    ApprovalRequired,
    WaitingForInput,
    Completed,
    Error,
}

public sealed record AgentEvent(
    string AdapterId,
    string SessionId,
    AgentStateKind State,
    DateTimeOffset Timestamp,
    string? Message = null,
    string? RequestId = null,
    string? TurnId = null);

public enum AgentCommandKind
{
    SubmitPrompt = 0,
    Interrupt,
    ApproveOnce,
    ApproveForSession,
    Decline,
    Cancel,
    NewSession,
    NextSession,
    PreviousSession,
    ReviewChanges,

    /// <summary>Switch the agent's permission mode; <c>Text</c> carries the
    /// mode name (see <see cref="AgentModes.PermissionModes"/>).</summary>
    SetPermissionMode,

    /// <summary>Compact the conversation so far, freeing context.</summary>
    CompactContext,

    /// <summary>Switch the model; <c>Text</c> carries the name or alias.</summary>
    SetModel,

    /// <summary>Switch the reasoning effort; <c>Text</c> carries the level.</summary>
    SetEffort,

    /// <summary>
    /// Step to the next model in <see cref="AgentModes.ModelCycle"/>. The host
    /// resolves this to a <see cref="SetModel"/> before it reaches an adapter,
    /// so adapters never track cycle position — a button that cycles is the
    /// natural controller idiom, but it is not a distinct agent operation.
    /// </summary>
    CycleModel,

    /// <summary>Step to the next level in <see cref="AgentModes.EffortCycle"/>;
    /// resolved to <see cref="SetEffort"/> by the host.</summary>
    CycleEffort,
}

/// <summary>
/// The values agents accept for the switchable session settings. Confirmed
/// against Claude Code 2.1.220, which reports them itself when a bare
/// <c>/model</c> or <c>/effort</c> is sent.
/// </summary>
public static class AgentModes
{
    /// <summary>
    /// Permission modes. "default" is absent from the CLI's own --help listing
    /// but is accepted, and is the mode sessions start in.
    /// </summary>
    public static readonly IReadOnlyList<string> PermissionModes =
        ["default", "plan", "acceptEdits", "auto", "dontAsk", "bypassPermissions"];

    /// <summary>Models offered by the cycle binding. Aliases, not pinned ids,
    /// so the cycle keeps working as new models ship.</summary>
    public static readonly IReadOnlyList<string> ModelCycle =
        ["default", "sonnet", "opus", "haiku", "fable"];

    /// <summary>Reasoning effort levels, lowest to highest.</summary>
    public static readonly IReadOnlyList<string> EffortCycle =
        ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Steps a cycle, wrapping, and tolerating an unknown current
    /// value by starting from the front.</summary>
    public static string Next(IReadOnlyList<string> cycle, string? current)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (cycle.Count == 0)
        {
            throw new ArgumentException("Cycle must not be empty.", nameof(cycle));
        }

        var index = current is null ? -1 : IndexOf(cycle, current);
        return cycle[(index + 1) % cycle.Count];
    }

    private static int IndexOf(IReadOnlyList<string> cycle, string value)
    {
        for (var index = 0; index < cycle.Count; index++)
        {
            if (string.Equals(cycle[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}

public sealed record AgentCommand(
    AgentCommandKind Kind,
    string? SessionId = null,
    string? RequestId = null,
    string? Text = null);

public sealed record AgentAdapterOptions(
    string WorkingDirectory,
    string? ExecutablePath = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

public interface IAgentAdapter : IAsyncDisposable
{
    string Id { get; }

    bool IsStarted { get; }

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentEvent> ReadEventsAsync(
        CancellationToken cancellationToken = default);

    ValueTask ExecuteAsync(
        AgentCommand command,
        CancellationToken cancellationToken = default);
}
