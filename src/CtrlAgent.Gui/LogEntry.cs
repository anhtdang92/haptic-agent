using Avalonia.Media;

namespace CtrlAgent.Gui;

/// <summary>
/// One event-stream line: a capture timestamp plus a message tinted by what
/// it reports (errors red, approvals amber, agent lifecycle cyan, successes
/// green) so the stream can be scanned without reading every line.
/// </summary>
public sealed class LogEntry
{
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#B9CCEF"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF7A8C"));
    private static readonly IBrush ApprovalBrush = new SolidColorBrush(Color.Parse("#FFB020"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#34F5A4"));
    private static readonly IBrush AgentBrush = new SolidColorBrush(Color.Parse("#7DD3FC"));

    public required string Timestamp { get; init; }

    public required string Message { get; init; }

    public required IBrush Brush { get; init; }

    public static LogEntry Create(string message) => new()
    {
        Timestamp = DateTimeOffset.Now.ToString("HH:mm:ss"),
        Message = message,
        Brush = Classify(message),
    };

    private static IBrush Classify(string message)
    {
        if (ContainsAny(message, "error", "failed", "fail", "crash", "exception", "could not", "declined", "denied"))
        {
            return ErrorBrush;
        }

        if (ContainsAny(message, "approval", "approve", "permission"))
        {
            return ApprovalBrush;
        }

        if (ContainsAny(message, "completed", "connected", "ready", "applied", "validated"))
        {
            return SuccessBrush;
        }

        if (ContainsAny(message, "agent", "session", "turn", "prompt", "command"))
        {
            return AgentBrush;
        }

        return DefaultBrush;
    }

    private static bool ContainsAny(string message, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (message.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
