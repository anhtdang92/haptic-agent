using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CtrlAgent.Core;

namespace CtrlAgent.Adapters.Mock;

public sealed class MockAgentAdapter : IAgentAdapter
{
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeTurn;
    private int _sessionNumber = 1;
    private bool _disposed;

    public string Id => "mock";

    public bool IsStarted { get; private set; }

    public string CurrentSessionId => $"mock-{_sessionNumber}";

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsStarted)
        {
            return ValueTask.CompletedTask;
        }

        IsStarted = true;
        Publish(AgentStateKind.Idle, "Mock adapter ready.");
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<AgentEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var agentEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return agentEvent;
        }
    }

    public ValueTask ExecuteAsync(
        AgentCommand command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsStarted)
        {
            throw new InvalidOperationException("The adapter must be started before executing commands.");
        }

        switch (command.Kind)
        {
            case AgentCommandKind.NewSession:
                CancelActiveTurn();
                _sessionNumber++;
                Publish(AgentStateKind.Idle, $"Created {CurrentSessionId}.");
                break;

            case AgentCommandKind.SubmitPrompt:
                StartMockTurn(command.Text ?? "Inspect the project and suggest the next useful change.");
                break;

            case AgentCommandKind.Interrupt:
            case AgentCommandKind.Cancel:
                CancelActiveTurn();
                Publish(AgentStateKind.Idle, "Turn interrupted.");
                break;

            case AgentCommandKind.ApproveOnce:
            case AgentCommandKind.ApproveForSession:
                Publish(AgentStateKind.Working, "Approval accepted.", command.RequestId);
                _ = CompleteAfterDelayAsync(command.RequestId, _lifetime.Token);
                break;

            case AgentCommandKind.Decline:
                Publish(AgentStateKind.Completed, "Approval declined.", command.RequestId);
                break;

            case AgentCommandKind.SetPermissionMode:
                Publish(AgentStateKind.Idle, $"Mock permission mode set to '{command.Text ?? "default"}'.");
                break;

            case AgentCommandKind.CompactContext:
                Publish(AgentStateKind.Idle, "Mock context compacted.");
                break;

            case AgentCommandKind.SetModel:
                Publish(AgentStateKind.Idle, $"Mock model set to '{command.Text ?? "default"}'.");
                break;

            case AgentCommandKind.SetEffort:
                Publish(AgentStateKind.Idle, $"Mock effort set to '{command.Text ?? "medium"}'.");
                break;

            case AgentCommandKind.ReviewChanges:
                StartMockTurn("Review all current changes.");
                break;

            case AgentCommandKind.NextSession:
                CancelActiveTurn();
                _sessionNumber++;
                Publish(AgentStateKind.Idle, $"Switched to {CurrentSessionId}.");
                break;

            case AgentCommandKind.PreviousSession:
                CancelActiveTurn();
                if (_sessionNumber > 1)
                {
                    _sessionNumber--;
                    Publish(AgentStateKind.Idle, $"Switched to {CurrentSessionId}.");
                }
                else
                {
                    Publish(AgentStateKind.Idle, $"Already at the first session ({CurrentSessionId}).");
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unsupported mock command.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        CancelActiveTurn();
        _lifetime.Cancel();
        _events.Writer.TryComplete();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    private void StartMockTurn(string prompt)
    {
        CancelActiveTurn();
        _activeTurn = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        // Describe the work rather than echoing the prompt verbatim — a bare
        // echo shows up in the conversation view as the agent repeating the
        // user back.
        Publish(AgentStateKind.Working, $"Working on: {Summarize(prompt)}");
        _ = RunMockTurnAsync(prompt, _activeTurn.Token);
    }

    private async Task RunMockTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken).ConfigureAwait(false);

            if (prompt.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("delete", StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    AgentStateKind.ApprovalRequired,
                    "Mock command requires approval.",
                    $"approval-{Guid.NewGuid():N}");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
            Publish(AgentStateKind.Completed, "Mock turn completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when interrupted or disposed.
        }
    }

    private async Task CompleteAfterDelayAsync(string? requestId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken).ConfigureAwait(false);
            Publish(AgentStateKind.Completed, "Approved mock operation completed.", requestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string Summarize(string prompt)
    {
        var singleLine = prompt.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= 60 ? singleLine : singleLine[..60] + "…";
    }

    private void CancelActiveTurn()
    {
        _activeTurn?.Cancel();
        _activeTurn?.Dispose();
        _activeTurn = null;
    }

    private void Publish(AgentStateKind state, string message, string? requestId = null)
    {
        _events.Writer.TryWrite(new AgentEvent(
            Id,
            CurrentSessionId,
            state,
            DateTimeOffset.UtcNow,
            message,
            requestId));
    }
}
