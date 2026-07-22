using System.Runtime.CompilerServices;
using CtrlAgent.Adapters.Mock;
using CtrlAgent.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Rumble frames clamp motor values", TestRumbleClampingAsync),
    ("Feedback router selects completion cue", TestFeedbackRoutingAsync),
    ("Looping haptics do not block the event loop", TestNonBlockingHapticSchedulerAsync),
    ("Plain A submits a prompt", TestPlainMappingAsync),
    ("LB+A overrides plain A", TestChordPriorityAsync),
    ("Approval chord does not fall through", TestApprovalSafetyAsync),
    ("Pending approval hydrates request id", TestApprovalMappingAsync),
    ("Tap and hold split on release duration", TestTapVersusHoldAsync),
    ("Double press fires inside its window", TestDoublePressAsync),
    ("Profile JSON round-trips", TestProfileJsonRoundTripAsync),
    ("Unsafe or ambiguous profiles are rejected", TestProfileValidationAsync),
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
    AssertEqual("completed", pattern!.Name);
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

static Task TestTapVersusHoldAsync()
{
    var profile = new ControllerProfile(
        "tap-hold",
        [
            new(ControllerControl.A, InputGesture.Tap, AgentCommandKind.ReviewChanges),
            new(ControllerControl.A, InputGesture.Hold, AgentCommandKind.Interrupt),
        ]);
    var engine = new MappingEngine(profile);
    var start = DateTimeOffset.UtcNow;

    _ = engine.Process(At(ControllerControl.A, ControllerInputEventKind.Pressed, start));
    var tap = engine.Process(At(
        ControllerControl.A,
        ControllerInputEventKind.Released,
        start + TimeSpan.FromMilliseconds(150)));

    AssertEqual(1, tap.Count);
    AssertEqual(AgentCommandKind.ReviewChanges, tap[0].Kind);

    var second = start + TimeSpan.FromSeconds(2);
    _ = engine.Process(At(ControllerControl.A, ControllerInputEventKind.Pressed, second));
    var hold = engine.Process(At(
        ControllerControl.A,
        ControllerInputEventKind.Released,
        second + TimeSpan.FromMilliseconds(600)));

    AssertEqual(1, hold.Count);
    AssertEqual(AgentCommandKind.Interrupt, hold[0].Kind);
    return Task.CompletedTask;
}

static Task TestDoublePressAsync()
{
    var profile = new ControllerProfile(
        "double",
        [
            new(ControllerControl.B, InputGesture.DoublePress, AgentCommandKind.NewSession),
        ]);
    var engine = new MappingEngine(profile);
    var start = DateTimeOffset.UtcNow;

    AssertEqual(0, engine.Process(At(ControllerControl.B, ControllerInputEventKind.Pressed, start)).Count);
    _ = engine.Process(At(ControllerControl.B, ControllerInputEventKind.Released, start + TimeSpan.FromMilliseconds(60)));

    var second = engine.Process(At(
        ControllerControl.B,
        ControllerInputEventKind.Pressed,
        start + TimeSpan.FromMilliseconds(200)));
    AssertEqual(1, second.Count);
    AssertEqual(AgentCommandKind.NewSession, second[0].Kind);
    _ = engine.Process(At(ControllerControl.B, ControllerInputEventKind.Released, start + TimeSpan.FromMilliseconds(260)));

    // The completed double-press resets the sequence, so a third press within
    // the window starts a new pair instead of firing again.
    AssertEqual(0, engine.Process(At(
        ControllerControl.B,
        ControllerInputEventKind.Pressed,
        start + TimeSpan.FromMilliseconds(400))).Count);

    // Presses spaced beyond the window never fire.
    var late = start + TimeSpan.FromSeconds(5);
    _ = engine.Process(At(ControllerControl.B, ControllerInputEventKind.Released, start + TimeSpan.FromMilliseconds(460)));
    AssertEqual(0, engine.Process(At(ControllerControl.B, ControllerInputEventKind.Pressed, late)).Count);
    return Task.CompletedTask;
}

static Task TestProfileJsonRoundTripAsync()
{
    var json = ControllerProfileJson.Serialize(ControllerProfile.Default);
    var profile = ControllerProfileJson.Deserialize(json);

    AssertEqual(ControllerProfile.Default.Name, profile.Name);
    AssertEqual(ControllerProfile.Default.Bindings.Count, profile.Bindings.Count);

    var approveChord = profile.Bindings.Single(binding =>
        binding.Command == AgentCommandKind.ApproveOnce && binding.Modifiers is { Count: > 0 });
    Assert(approveChord.RequiresPendingApproval, "Approval flag must survive the round-trip.");
    Assert(
        approveChord.Modifiers!.Contains(ControllerControl.RightShoulder),
        "Chord modifier must survive the round-trip.");
    return Task.CompletedTask;
}

static Task TestProfileValidationAsync()
{
    AssertEqual(0, ControllerProfileValidator.Validate(ControllerProfile.Default).Count);

    // A bare face-button approval is careless even with the pending flag set.
    var careless = new ControllerProfile(
        "careless",
        [
            new(ControllerControl.A, InputGesture.Press, AgentCommandKind.ApproveOnce, RequiresPendingApproval: true),
        ]);
    Assert(ControllerProfileValidator.Validate(careless).Count > 0, "Expected careless approval to be rejected.");

    var threw = false;
    try
    {
        _ = new MappingEngine(careless);
    }
    catch (ArgumentException)
    {
        threw = true;
    }

    Assert(threw, "MappingEngine must refuse an invalid profile.");

    // Approval-family bindings must be gated on a pending request.
    var noPending = new ControllerProfile(
        "no-pending",
        [
            new(ControllerControl.PaddleLeft1, InputGesture.Press, AgentCommandKind.ApproveOnce),
        ]);
    Assert(ControllerProfileValidator.Validate(noPending).Count > 0, "Expected missing pending flag to be rejected.");

    // Press combined with Hold on the same chord double-fires one physical action.
    var ambiguous = new ControllerProfile(
        "ambiguous",
        [
            new(ControllerControl.A, InputGesture.Press, AgentCommandKind.SubmitPrompt),
            new(ControllerControl.A, InputGesture.Hold, AgentCommandKind.Interrupt),
        ]);
    Assert(ControllerProfileValidator.Validate(ambiguous).Count > 0, "Expected Press+Hold mix to be rejected.");
    return Task.CompletedTask;
}

static ControllerInputEvent Press(ControllerControl control) =>
    new("test-controller", control, ControllerInputEventKind.Pressed, 1f, DateTimeOffset.UtcNow);

static ControllerInputEvent At(
    ControllerControl control,
    ControllerInputEventKind kind,
    DateTimeOffset timestamp) =>
    new("test-controller", control, kind, kind == ControllerInputEventKind.Released ? 0f : 1f, timestamp);

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
