using HapticAgent.Core;

var router = new FeedbackRouter();
var sessionId = "demo-session";

AgentStateKind[] states =
[
    AgentStateKind.Working,
    AgentStateKind.ApprovalRequired,
    AgentStateKind.WaitingForInput,
    AgentStateKind.Completed,
    AgentStateKind.Error,
];

Console.WriteLine("HapticAgent feedback routing demo");
Console.WriteLine("This demo prints motor frames; it does not access controller hardware yet.");
Console.WriteLine();

foreach (var state in states)
{
    var agentEvent = new AgentEvent(
        AdapterId: "mock",
        SessionId: sessionId,
        State: state,
        Timestamp: DateTimeOffset.UtcNow);

    var pattern = router.Route(agentEvent);

    Console.WriteLine($"{state,-20} -> {pattern?.Name ?? "no cue"}");

    if (pattern is null)
    {
        continue;
    }

    for (var index = 0; index < pattern.Frames.Count; index++)
    {
        var frame = pattern.Frames[index];
        Console.WriteLine(
            $"  frame {index + 1}: " +
            $"low={frame.LowFrequency:0.00}, " +
            $"high={frame.HighFrequency:0.00}, " +
            $"leftTrigger={frame.LeftTrigger:0.00}, " +
            $"rightTrigger={frame.RightTrigger:0.00}, " +
            $"duration={frame.Duration.TotalMilliseconds:0}ms");
    }

    Console.WriteLine($"  loop={pattern.Loop}, total={pattern.Duration.TotalMilliseconds:0}ms");
    Console.WriteLine();
}
