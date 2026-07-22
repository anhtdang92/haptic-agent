namespace HapticAgent.Core;

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
