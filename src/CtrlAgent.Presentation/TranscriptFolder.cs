using System.Text;
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
/// Cleans process- and terminal-originated text before it reaches Avalonia.
/// It strips ANSI control sequences and non-printing C0 controls while keeping
/// line breaks, tabs, accents, non-Latin scripts, and ordinary Unicode prose.
/// Known activity glyphs are converted to words because the bundled Inter font
/// does not contain them and Windows fallback can display boxes or blobs.
/// </summary>
public static class TranscriptText
{
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var source = value
            .Replace("→ ⚠ ", "Warning — ", StringComparison.Ordinal)
            .Replace("→ ", string.Empty, StringComparison.Ordinal)
            .Replace("✓", "Done", StringComparison.Ordinal)
            .Replace("✕", "Error", StringComparison.Ordinal)
            .Replace("🔒", "Approval", StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormC);

        var result = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];

            if (current == '\u001b')
            {
                index = SkipEscapeSequence(source, index);
                continue;
            }

            if (current < ' ' && current is not '\r' and not '\n' and not '\t')
            {
                continue;
            }

            if (current == '\u007f')
            {
                continue;
            }

            result.Append(current);
        }

        return result.ToString();
    }

    private static int SkipEscapeSequence(string text, int escapeIndex)
    {
        var index = escapeIndex + 1;
        if (index >= text.Length)
        {
            return escapeIndex;
        }

        // CSI: ESC [ parameters/intermediates final-byte
        if (text[index] == '[')
        {
            index++;
            while (index < text.Length)
            {
                var value = text[index];
                if (value is >= '\u0040' and <= '\u007e')
                {
                    return index;
                }

                index++;
            }

            return text.Length - 1;
        }

        // OSC: ESC ] ... BEL, or ESC ] ... ESC \
        if (text[index] == ']')
        {
            index++;
            while (index < text.Length)
            {
                if (text[index] == '\a')
                {
                    return index;
                }

                if (text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '\\')
                {
                    return index + 1;
                }

                index++;
            }

            return text.Length - 1;
        }

        // A two-byte ANSI escape such as ESC c.
        return index;
    }
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

        var message = TranscriptText.Clean(agentEvent.Message);
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
