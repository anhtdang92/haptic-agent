namespace CtrlAgent.Gui;

/// <summary>
/// One row of the conversation transcript. Prose renders as a chat bubble
/// (user right, agent left); activity rows (tool calls, plan progress,
/// results, approvals) render as small dim status lines between bubbles —
/// the Claude-app layout. Text is mutable so a streaming reply can update
/// its bubble in place.
/// </summary>
public sealed class ChatMessage : ViewModelBase
{
    private string _text = string.Empty;

    public required bool IsUser { get; init; }

    public required bool IsActivity { get; init; }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    private static readonly string[] ActivityPrefixes =
    [
        "Thinking…",
        "Plan ",
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

    /// <summary>Tool chatter and progress lines are activity; the rest is prose.</summary>
    public static bool IsActivityText(string message)
    {
        foreach (var prefix in ActivityPrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
