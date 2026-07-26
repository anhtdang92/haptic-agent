using Avalonia.Media;
using CtrlAgent.Presentation;

namespace CtrlAgent.Gui;

/// <summary>
/// One event-stream line: a capture timestamp plus a message tinted by what it
/// reports (errors red, approvals amber, agent lifecycle cyan, successes green)
/// so the stream can be scanned without reading every line.
/// <para>
/// Only the colour lives here. Deciding <em>which</em> severity a line is
/// belongs to <see cref="LogClassifier"/>, where a test can reach it — this
/// project cannot be referenced from the test harness (see CtrlAgent.Presentation).
/// </para>
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

    /// <summary>Raw controller input lines, so the stream can hide them.</summary>
    public required bool IsControllerEvent { get; init; }

    public static LogEntry Create(string message) => new()
    {
        Timestamp = DateTimeOffset.Now.ToString("HH:mm:ss"),
        Message = message,
        Brush = BrushFor(LogClassifier.Classify(message)),
        IsControllerEvent = LogClassifier.IsControllerEvent(message),
    };

    private static IBrush BrushFor(LogSeverity severity) => severity switch
    {
        LogSeverity.Error => ErrorBrush,
        LogSeverity.Approval => ApprovalBrush,
        LogSeverity.Success => SuccessBrush,
        LogSeverity.Agent => AgentBrush,
        _ => DefaultBrush,
    };
}
