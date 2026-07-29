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

    /// <summary>Template selectors for surfaces that render this row three
    /// ways (activity line, user bubble, agent bubble) — compiled bindings
    /// cannot express the boolean combination inline.</summary>
    public bool IsUserBubble => IsUser && !IsActivity;

    public bool IsAgentBubble => !IsUser && !IsActivity;

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }
}
