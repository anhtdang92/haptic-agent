using CtrlAgent.Core;

namespace CtrlAgent.Presentation;

/// <summary>How a log line should read at a glance. The GUI picks the colour.</summary>
public enum LogSeverity
{
    Normal,
    Agent,
    Success,
    Approval,
    Error,
}

/// <summary>
/// Classifies event-stream lines so the log can be scanned rather than read.
/// <para>
/// The rules are keyword matches over text the app itself emits, checked
/// most-severe first — "Approval declined" has to read as an error, not as an
/// approval, and it contains both words. Order <em>is</em> the rule, which is
/// why it lives somewhere a test can pin it.
/// </para>
/// </summary>
public static class LogClassifier
{
    /// <summary>Marks raw controller input, which the stream can filter out.</summary>
    public const string ControllerPrefix = "[controller]";

    private static readonly string[] ErrorWords =
        ["error", "failed", "fail", "crash", "exception", "could not", "declined", "denied"];

    private static readonly string[] ApprovalWords = ["approval", "approve", "permission"];

    private static readonly string[] SuccessWords =
        ["completed", "connected", "ready", "applied", "validated"];

    private static readonly string[] AgentWords = ["agent", "session", "turn", "prompt", "command"];

    public static bool IsControllerEvent(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.StartsWith(ControllerPrefix, StringComparison.Ordinal);
    }

    public static LogSeverity Classify(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (ContainsAny(message, ErrorWords))
        {
            return LogSeverity.Error;
        }

        if (ContainsAny(message, ApprovalWords))
        {
            return LogSeverity.Approval;
        }

        if (ContainsAny(message, SuccessWords))
        {
            return LogSeverity.Success;
        }

        return ContainsAny(message, AgentWords) ? LogSeverity.Agent : LogSeverity.Normal;
    }

    private static bool ContainsAny(string message, string[] needles)
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

/// <summary>Which physical controls can answer a pending approval.</summary>
public static class ApprovalControls
{
    /// <summary>
    /// Every control involved in an approval binding, chord modifiers
    /// included. The modifiers matter: highlighting only the A of "RB+A"
    /// tells the user to press the one button that will not work.
    /// </summary>
    public static IReadOnlyCollection<ControllerControl> From(ControllerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var controls = new HashSet<ControllerControl>();
        foreach (var binding in profile.Bindings)
        {
            if (!binding.RequiresPendingApproval)
            {
                continue;
            }

            controls.Add(binding.Control);
            if (binding.Modifiers is { Count: > 0 })
            {
                foreach (var modifier in binding.Modifiers)
                {
                    controls.Add(modifier);
                }
            }
        }

        return controls;
    }
}
