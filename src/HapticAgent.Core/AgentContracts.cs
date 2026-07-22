namespace HapticAgent.Core;

/// <summary>
/// Agent lifecycle states that controller feedback can react to without depending
/// on any one agent's protocol.
/// </summary>
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

/// <summary>
/// An event emitted by an agent adapter.
/// </summary>
public sealed record AgentEvent(
    string AdapterId,
    string SessionId,
    AgentStateKind State,
    DateTimeOffset Timestamp,
    string? Message = null,
    string? RequestId = null);

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

/// <summary>
/// A normalized command produced by a controller mapping.
/// </summary>
public sealed record AgentCommand(
    AgentCommandKind Kind,
    string? SessionId = null,
    string? RequestId = null,
    string? Text = null);

/// <summary>
/// Converts agent-specific events and commands at the protocol boundary.
/// </summary>
public interface IAgentAdapter : IAsyncDisposable
{
    string Id { get; }

    IAsyncEnumerable<AgentEvent> ReadEventsAsync(
        CancellationToken cancellationToken = default);

    ValueTask ExecuteAsync(
        AgentCommand command,
        CancellationToken cancellationToken = default);
}
