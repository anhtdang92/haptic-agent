using CtrlAgent.Core;

namespace CtrlAgent.Presentation;

/// <summary>What a single agent event does to the conversation view.</summary>
public abstract record TranscriptAction
{
    /// <summary>The event contributes nothing readable.</summary>
    public sealed record None : TranscriptAction;

    /// <summary>Start a new agent prose bubble and stream into it.</summary>
    public sealed record StartBubble(string Text) : TranscriptAction;

    /// <summary>Replace the current streaming bubble's text in place.</summary>
    public sealed record UpdateBubble(string Text) : TranscriptAction;

    /// <summary>Add a dim status row between bubbles.</summary>
    public sealed record AddActivity(string Text) : TranscriptAction;
}

/// <summary>
/// Folds the agent's event stream into conversation rows, Claude-app style:
/// prose accumulates into one bubble that updates in place while it streams,
/// and anything that is not prose — tool calls, plan progress, results,
/// approvals — lands as a dim activity row and <em>closes</em> the open bubble
/// so the next prose chunk starts a fresh one.
/// <para>
/// The bubble-closing rule is the whole reason this is a class and not a
/// switch inlined in a view model. Get it wrong in one direction and a reply
/// interrupted by a tool call keeps overwriting the same bubble, so the user
/// watches earlier text disappear; get it wrong in the other and every
/// streamed chunk becomes its own bubble, which is a wall of fragments.
/// </para>
/// Stateful and single-threaded: one instance per conversation.
/// </summary>
public sealed class TranscriptFolder
{
    private bool _isStreaming;

    /// <summary>True while a prose bubble is open and being streamed into.</summary>
    public bool IsStreaming => _isStreaming;

    /// <summary>Forget the open bubble — a new session or workspace starts clean.</summary>
    public void Reset() => _isStreaming = false;

    public TranscriptAction Fold(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        var message = agentEvent.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return new TranscriptAction.None();
        }

        switch (agentEvent.State)
        {
            case AgentStateKind.Working when ActivityText.IsActivity(message):
                return CloseAndAdd(message);

            case AgentStateKind.Working:
                if (_isStreaming)
                {
                    return new TranscriptAction.UpdateBubble(message);
                }

                _isStreaming = true;
                return new TranscriptAction.StartBubble(message);

            // Word markers, not glyphs: the app ships Inter, which has no ✓,
            // ✕, or 🔒 — on Windows those fall back to Segoe's symbol/emoji
            // fonts and render as grey blobs or empty boxes, and the lock sat
            // on every approval row. Verified on a real launch, invisible in
            // headless shots only until you know to look.
            case AgentStateKind.Completed:
                return CloseAndAdd($"Done — {message}");

            case AgentStateKind.Error:
                return CloseAndAdd($"Error — {message}");

            // Deliberately does not close the bubble: an approval interrupts a
            // reply mid-sentence, and the rest of that sentence belongs to the
            // same bubble once the user answers.
            case AgentStateKind.ApprovalRequired:
            case AgentStateKind.WaitingForInput:
                return new TranscriptAction.AddActivity($"Needs approval — {message}");

            case AgentStateKind.Idle:
                return CloseAndAdd(message);

            default:
                return new TranscriptAction.None();
        }
    }

    private TranscriptAction CloseAndAdd(string text)
    {
        _isStreaming = false;
        return new TranscriptAction.AddActivity(text);
    }
}
