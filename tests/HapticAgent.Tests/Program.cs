using System.Runtime.CompilerServices;
using HapticAgent.Adapters.Mock;
using HapticAgent.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Rumble frames clamp motor values", TestRumbleClampingAsync),
    ("Feedback router selects completion cue", TestFeedbackRoutingAsync),
    ("Looping haptics do not block the event loop", TestNonBlockingHapticSchedulerAsync),
    ("Plain A submits a prompt", TestPlainMappingAsync),
    ("LB+A overrides plain A", TestChordPriorityAsync),
    ("Approval chord does not fall through", TestApprovalSafetyAsync),
    ("Pending approval hydrates request id", TestApprovalMappingAsync),
    ("Mock adapter emits approval lifecycle", TestMockAdapterAsync),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static Task TestRumbleClampingAsync()
{
    var frame = RumbleFrame.Create(
        -1f,
        2f,
        0.4f,
        0.6f,
        TimeSpan.FromMilliseconds(10));

    AssertEqual(0f, frame.LowFrequency);
    AssertEqual(1f, frame.HighFrequency);
    AssertEqual(0.4f, frame.LeftTrigger);
    AssertEqual(0.6f, frame.RightTrigger);
    return Task.CompletedTask;
}

static Task TestFeedbackRoutingAsync()
{
    var router = new FeedbackRouter();
    var pattern = router.Route(new AgentEvent(
        "test",
        "session",
        AgentStateKind.Completed,
        DateTimeOffset.UtcNow));

    Assert(pattern is not null, "Expected a haptic pattern.");
    AssertEqual("completed", pattern.Name);
    return Task.CompletedTask;
}

static async Task TestNonBlockingHapticSchedulerAsync()
{
    await using var controller = new BlockingController();
    await using var scheduler = new HapticScheduler(controller);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

    await scheduler.PlayAsync(HapticPatternCatalog.ApprovalRequired, timeout.Token)
        .ConfigureAwait(false);
    await controller.Started.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

    Assert(
        !controller.Completed.Task.IsCompleted,
        "A looping cue should still be running after PlayAsync returns.");

    await scheduler.StopAsync(timeout.Token).ConfigureAwait(false);
    await controller.Completed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

    Assert(controller.StopCount > 0, "Stopping the scheduler should stop controller haptics.");
}

static Task TestPlainMappingAsync()
{
    var engine = new MappingEngine(ControllerProfile.Default);
    var commands = engine.Process(Press(ControllerControl.A));

    AssertEqual(1, commands.Count);
    AssertEqual(AgentCommandKind.SubmitPrompt, commands[0].Kind);
    return Task.CompletedTask;
}

static Task TestChordPriorityAsync()
{
    var engine = new MappingEngine(ControllerProfile.Default);
    _ = engine.Process(Press(ControllerControl.LeftShoulder));
    var commands = engine.Process(Press(ControllerControl.A));

    AssertEqual(1, commands.Count);
    AssertEqual(AgentCommandKind.SubmitPrompt, commands[0].Kind);
    Assert(
        commands[0].Text?.Contains("test suite", StringComparison.OrdinalIgnoreCase) == true,
        "Expected the specialized test prompt.");
    return Task.CompletedTask;
}

static Task TestApprovalSafetyAsync()
{
    var engine = new MappingEngine(ControllerProfile.Default);
    _ = engine.Process(Press(ControllerControl.RightShoulder));
    var commands = engine.Process(Press(ControllerControl.A));

    AssertEqual(0, commands.Count);
    return Task.CompletedTask;
}

static Task TestApprovalMappingAsync()
{
    var engine = new MappingEngine(ControllerProfile.Default);
    engine.SetPendingApproval("session-1", "41");
    _ = engine.Process(Press(ControllerControl.RightShoulder));
    var commands = engine.Process(Press(ControllerControl.A));

    AssertEqual(1, commands.Count);
    AssertEqual(AgentCommandKind.ApproveOnce, commands[0].Kind);
    AssertEqual("session-1", commands[0].SessionId);
    AssertEqual("41", commands[0].RequestId);
    return Task.CompletedTask;
}

static async Task TestMockAdapterAsync()
{
    await using var adapter = new MockAgentAdapter();
    await adapter.StartAsync().ConfigureAwait(false);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    await using var enumerator = adapter.ReadEventsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected initial idle event.");
    AssertEqual(AgentStateKind.Idle, enumerator.Current.State);

    await adapter.ExecuteAsync(new AgentCommand(
        AgentCommandKind.SubmitPrompt,
        Text: "Delete a generated file after approval.")).ConfigureAwait(false);

    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected working event.");
    AssertEqual(AgentStateKind.Working, enumerator.Current.State);

    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected approval event.");
    AssertEqual(AgentStateKind.ApprovalRequired, enumerator.Current.State);
    Assert(!string.IsNullOrWhiteSpace(enumerator.Current.RequestId), "Expected approval request id.");
}

static ControllerInputEvent Press(ControllerControl control) =>
    new("test-controller", control, ControllerInputEventKind.Pressed, 1f, DateTimeOffset.UtcNow);

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

internal sealed class BlockingController : IControllerDevice
{
    private int _stopCount;

    public TaskCompletionSource<bool> Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> Completed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id => "blocking-controller";

    public string DisplayName => "Blocking test controller";

    public ControllerCapabilities Capabilities { get; } = new(
        HasFourPaddles: true,
        HasLowFrequencyRumble: true,
        HasHighFrequencyRumble: true,
        HasLeftTriggerRumble: true,
        HasRightTriggerRumble: true);

    public bool IsConnected => true;

    public int StopCount => Volatile.Read(ref _stopCount);

    public async IAsyncEnumerable<ControllerInputEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    public async ValueTask PlayAsync(
        HapticPattern pattern,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        Started.TrySetResult(true);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Completed.TrySetResult(true);
        }
    }

    public ValueTask StopHapticsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _stopCount);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Completed.TrySetResult(true);
        return ValueTask.CompletedTask;
    }
}
