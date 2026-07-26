using CtrlAgent.Core;

namespace CtrlAgent.Presentation;

/// <summary>
/// Human wording for <see cref="AgentStateKind"/>.
/// <para>
/// The UI used to print <c>State.ToString()</c>, so people read
/// "ApprovalRequired" and "WaitingForInput" — C# enum names, in an app that
/// otherwise speaks English. They were also the two longest strings on the
/// command bar, so entering either state reflowed it and shifted everything
/// below by a row.
/// </para>
/// </summary>
public static class AgentStateText
{
    public static string Describe(AgentStateKind state) => state switch
    {
        AgentStateKind.Idle => "idle",
        AgentStateKind.Working => "working",
        AgentStateKind.ApprovalRequired => "approval",
        AgentStateKind.WaitingForInput => "waiting",
        AgentStateKind.Completed => "done",
        AgentStateKind.Error => "error",
        _ => "—",
    };
}
