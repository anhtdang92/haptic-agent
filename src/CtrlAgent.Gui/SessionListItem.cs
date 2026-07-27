using System.Windows.Input;

namespace CtrlAgent.Gui;

/// <summary>
/// One row of the recents sidebar: a session found in the agent's on-disk
/// store. Rows are immutable snapshots — the list is rebuilt on refresh, so
/// nothing here needs change notification. Each row carries its own resume
/// command so the item template binds locally instead of reaching for the
/// window's DataContext.
/// </summary>
public sealed class SessionListItem
{
    public SessionListItem(string sessionId, string title, DateTimeOffset lastActivity, bool isCurrent, ICommand resume)
    {
        SessionId = sessionId;
        Title = title;
        WhenLabel = Describe(lastActivity);
        IsCurrent = isCurrent;
        ResumeCommand = resume;
    }

    public string SessionId { get; }

    public string Title { get; }

    public string WhenLabel { get; }

    public bool IsCurrent { get; }

    public ICommand ResumeCommand { get; }

    /// <summary>"just now" / "12m ago" / "3h ago" / "2d ago" — a recents list
    /// needs rough age, not timestamps.</summary>
    private static string Describe(DateTimeOffset lastActivity)
    {
        var age = DateTimeOffset.UtcNow - lastActivity.ToUniversalTime();
        return age switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes}m ago",
            { TotalDays: < 1 } => $"{(int)age.TotalHours}h ago",
            { TotalDays: < 30 } => $"{(int)age.TotalDays}d ago",
            _ => lastActivity.ToLocalTime().ToString("yyyy-MM-dd"),
        };
    }
}
