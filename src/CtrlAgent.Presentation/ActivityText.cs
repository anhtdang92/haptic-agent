namespace CtrlAgent.Presentation;

/// <summary>
/// Tells the agent's prose apart from its chatter. Prose earns a chat bubble;
/// tool calls, plan progress and tool output render as dim activity rows.
/// <para>
/// Prefix matching is deliberate and deliberately narrow: the adapters build
/// these strings themselves (see the Claude adapter's tool descriptions), so
/// the set is closed rather than heuristic. A looser rule — "short lines are
/// activity", "lines with a colon are activity" — would demote real replies
/// into grey status text, which is far worse than the reverse.
/// </para>
/// </summary>
public static class ActivityText
{
    private static readonly string[] Prefixes =
    [
        "Thinking…",
        "Plan ",
        "→ ",
        "Commands: ",
        "Using tool:",
        "Prompt sent",
        "Bash: ",
        "Edit: ",
        "Write: ",
        "Read: ",
        "NotebookEdit: ",
        "Grep: ",
        "Glob: ",
        "WebFetch: ",
        "WebSearch: ",
        "Task: ",
    ];

    public static bool IsActivity(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        foreach (var prefix in Prefixes)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
