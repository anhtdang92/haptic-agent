using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CtrlAgent.Adapters.ClaudeCode;
using CtrlAgent.Adapters.Mock;
using CtrlAgent.Core;
using CtrlAgent.Hosting;

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
    ("Haptic hub survives detach and device loss", TestHapticHubAsync),
    ("Validation report computes go/no-go gates", TestValidationReportGatesAsync),
    ("Validation report renders evidence markdown", TestValidationReportMarkdownAsync),
    ("Claude stream parser classifies protocol messages", TestClaudeStreamParserAsync),
    ("Claude permission responses carry session rules", TestClaudePermissionResponseAsync),
    ("Host engine runs press-to-approval loop end to end", TestHostEngineEndToEndAsync),
    ("Host engine swaps profiles at runtime with validation", TestHostEngineProfileSwapAsync),
    ("Mock adapter emits approval lifecycle", TestMockAdapterAsync),
    ("Mock adapter navigates sessions", TestMockSessionNavigationAsync),
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

static async Task TestHapticHubAsync()
{
    await using var controller = new BlockingController();
    var scheduler = new HapticScheduler(controller);
    var hub = new HapticSchedulerHub();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

    // Detached hub calls are silent no-ops.
    await hub.PlayAsync(HapticPatternCatalog.Working, timeout.Token).ConfigureAwait(false);
    await hub.StopAsync(timeout.Token).ConfigureAwait(false);

    hub.Attach(scheduler);
    await hub.PlayAsync(HapticPatternCatalog.ApprovalRequired, timeout.Token).ConfigureAwait(false);
    await controller.Started.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

    hub.Detach(scheduler);
    await scheduler.DisposeAsync().ConfigureAwait(false);

    // After detach the hub routes nowhere and must not throw.
    await hub.PlayAsync(HapticPatternCatalog.Working, timeout.Token).ConfigureAwait(false);

    // Even a stale attach to a disposed scheduler is swallowed as device loss.
    hub.Attach(scheduler);
    await hub.PlayAsync(HapticPatternCatalog.Working, timeout.Token).ConfigureAwait(false);
    await hub.StopAsync(timeout.Token).ConfigureAwait(false);

    Assert(controller.StopCount > 0, "Disposing the scheduler should stop controller haptics.");
}

static async Task TestHostEngineEndToEndAsync()
{
    var controller = new ScriptedController();
    var provider = new SingleControllerProvider(controller);

    // The default prompt mentions "delete" so the mock adapter demands approval.
    await using var engine = new HostEngine(
        provider,
        new MockAgentAdapter(),
        ControllerProfile.Default,
        new HostEngineOptions("Please delete the generated file."));

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    engine.ControllerConnected += _ => connected.TrySetResult();
    engine.PendingApprovalChanged += message =>
    {
        if (message is not null)
        {
            pending.TrySetResult();
        }
        else if (pending.Task.IsCompleted)
        {
            cleared.TrySetResult();
        }
    };

    await engine.StartAsync(timeout.Token).ConfigureAwait(false);
    await connected.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

    // A press maps to SubmitPrompt through the default profile.
    controller.Emit(ControllerControl.A, ControllerInputEventKind.Pressed);
    controller.Emit(ControllerControl.A, ControllerInputEventKind.Released);

    await pending.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    Assert(
        await engine.RespondToApprovalAsync(AgentCommandKind.ApproveOnce).ConfigureAwait(false),
        "Expected a pending approval to answer.");

    await cleared.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    Assert(
        !await engine.RespondToApprovalAsync(AgentCommandKind.ApproveOnce).ConfigureAwait(false),
        "The pending approval must be cleared after it is answered.");
}

static async Task TestHostEngineProfileSwapAsync()
{
    var controller = new ScriptedController();
    await using var engine = new HostEngine(
        new SingleControllerProvider(controller),
        new MockAgentAdapter(),
        ControllerProfile.Default,
        new HostEngineOptions("prompt"));

    var applied = new TaskCompletionSource<ControllerProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
    engine.ProfileApplied += profile => applied.TrySetResult(profile);

    // An unsafe profile is rejected with errors and the active profile stays.
    var unsafeProfile = new ControllerProfile(
        "unsafe",
        [new(ControllerControl.A, InputGesture.Press, AgentCommandKind.ApproveOnce, RequiresPendingApproval: true)]);
    Assert(!engine.TryApplyProfile(unsafeProfile, out var errors), "Unsafe profile must be rejected.");
    Assert(errors.Count > 0, "Rejection must report errors.");
    AssertEqual("default", engine.Profile.Name);

    // A valid profile swaps in and raises ProfileApplied.
    var custom = new ControllerProfile(
        "custom",
        [new(ControllerControl.B, InputGesture.Press, AgentCommandKind.Interrupt)]);
    Assert(engine.TryApplyProfile(custom, out _), "Valid profile must apply.");
    AssertEqual("custom", engine.Profile.Name);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    AssertEqual("custom", (await applied.Task.WaitAsync(timeout.Token).ConfigureAwait(false)).Name);
}

static Task TestValidationReportGatesAsync()
{
    var report = SampleReport(
        standard: ValidationOutcome.Pass,
        reconnect: ValidationOutcome.Pass,
        rumble: ValidationOutcome.Pass,
        paddles: ValidationOutcome.Pass);
    Assert(report.IsGo, "All gates passing should be GO.");
    Assert(report.Recommendation.StartsWith("GO:", StringComparison.Ordinal), "Expected an unqualified GO.");

    var experimental = SampleReport(
        standard: ValidationOutcome.Pass,
        reconnect: ValidationOutcome.Pass,
        rumble: ValidationOutcome.Pass,
        paddles: ValidationOutcome.Skipped);
    Assert(experimental.IsGo, "Paddles must not block the GO gates.");
    Assert(
        experimental.Recommendation.Contains("experimental", StringComparison.OrdinalIgnoreCase),
        "Skipped paddles should downgrade to experimental.");

    var noGo = SampleReport(
        standard: ValidationOutcome.Pass,
        reconnect: ValidationOutcome.Fail,
        rumble: ValidationOutcome.Pass,
        paddles: ValidationOutcome.Pass);
    Assert(!noGo.IsGo, "A failing reconnect gate must be NO-GO.");
    Assert(
        noGo.Recommendation.Contains("reconnect", StringComparison.OrdinalIgnoreCase),
        "The NO-GO reason should name the failing gate.");
    return Task.CompletedTask;
}

static Task TestValidationReportMarkdownAsync()
{
    var report = SampleReport(
        standard: ValidationOutcome.Pass,
        reconnect: ValidationOutcome.Pass,
        rumble: ValidationOutcome.Pass,
        paddles: ValidationOutcome.Pass);
    var markdown = report.ToMarkdown();

    foreach (var expected in new[]
    {
        "## Environment",
        "## Pass/fail",
        "## Paddle observations",
        "## Rumble observations",
        "## Known anomalies",
        "## Recommendation",
        "Elite Test Pad",
        "| Standard controls | Pass |",
    })
    {
        Assert(markdown.Contains(expected, StringComparison.Ordinal), $"Markdown missing '{expected}'.");
    }

    AssertEqual(
        "2026-07-22-elite-series-2-usb.md",
        ValidationReport.SuggestFileName(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero), "USB"));
    return Task.CompletedTask;
}

static Task TestClaudeStreamParserAsync()
{
    var init = ParseClaudeLine("""{"type":"system","subtype":"init","cwd":"/repo","session_id":"sess-1","tools":[]}""");
    Assert(init is ClaudeStreamMessage.SessionInit { SessionId: "sess-1" }, "Expected SessionInit.");

    var text = ParseClaudeLine(
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Hello there"}]},"session_id":"sess-1"}""");
    Assert(text is ClaudeStreamMessage.AssistantActivity { Summary: "Hello there" }, "Expected assistant text summary.");

    var tool = ParseClaudeLine(
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]},"session_id":"sess-1"}""");
    Assert(tool is ClaudeStreamMessage.AssistantActivity { Summary: "Using tool: Bash" }, "Expected tool-use summary.");

    var success = ParseClaudeLine(
        """{"type":"result","subtype":"success","is_error":false,"result":"All done","session_id":"sess-1"}""");
    Assert(success is ClaudeStreamMessage.TurnResult { IsError: false, Summary: "All done" }, "Expected success result.");

    var failure = ParseClaudeLine(
        """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"sess-1"}""");
    Assert(failure is ClaudeStreamMessage.TurnResult { IsError: true }, "Expected error result.");

    var permission = ParseClaudeLine(
        """{"type":"control_request","request_id":"perm-1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm x"}}}""");
    Assert(
        permission is ClaudeStreamMessage.PermissionRequest { RequestId: "perm-1", ToolName: "Bash" },
        "Expected permission request.");
    var request = (ClaudeStreamMessage.PermissionRequest)permission;
    AssertEqual("rm x", request.Input.GetProperty("command").GetString());

    var canceled = ParseClaudeLine("""{"type":"control_cancel_request","request_id":"perm-1"}""");
    Assert(canceled is ClaudeStreamMessage.PermissionCanceled { RequestId: "perm-1" }, "Expected cancellation.");

    var noise = ParseClaudeLine("""{"type":"stream_event","event":{}}""");
    Assert(noise is ClaudeStreamMessage.Ignored, "Unknown message types must be ignored.");
    return Task.CompletedTask;
}

static ClaudeStreamMessage ParseClaudeLine(string json)
{
    using var document = JsonDocument.Parse(json);
    return ClaudeStreamParser.Parse(document.RootElement);
}

static Task TestClaudePermissionResponseAsync()
{
    using var inputDocument = JsonDocument.Parse("""{"command":"ls"}""");
    var input = inputDocument.RootElement.Clone();

    var once = JsonDocument.Parse(JsonSerializer.Serialize(
        ClaudePermissionResponse.Allow("req-1", "Bash", input, forSession: false))).RootElement;
    var onceResponse = once.GetProperty("response").GetProperty("response");
    AssertEqual("allow", onceResponse.GetProperty("behavior").GetString());
    AssertEqual("ls", onceResponse.GetProperty("updatedInput").GetProperty("command").GetString());
    Assert(!onceResponse.TryGetProperty("updatedPermissions", out _), "Approve-once must not add session rules.");
    AssertEqual("req-1", once.GetProperty("response").GetProperty("request_id").GetString());

    var session = JsonDocument.Parse(JsonSerializer.Serialize(
        ClaudePermissionResponse.Allow("req-2", "Bash", input, forSession: true))).RootElement;
    var sessionResponse = session.GetProperty("response").GetProperty("response");
    var rule = sessionResponse.GetProperty("updatedPermissions")[0];
    AssertEqual("addRules", rule.GetProperty("type").GetString());
    AssertEqual("allow", rule.GetProperty("behavior").GetString());
    AssertEqual("session", rule.GetProperty("destination").GetString());
    AssertEqual("Bash", rule.GetProperty("rules")[0].GetProperty("toolName").GetString());

    var deny = JsonDocument.Parse(JsonSerializer.Serialize(
        ClaudePermissionResponse.Deny("req-3", "Declined."))).RootElement;
    var denyResponse = deny.GetProperty("response").GetProperty("response");
    AssertEqual("deny", denyResponse.GetProperty("behavior").GetString());
    AssertEqual("Declined.", denyResponse.GetProperty("message").GetString());
    return Task.CompletedTask;
}

static ValidationReport SampleReport(
    ValidationOutcome standard,
    ValidationOutcome reconnect,
    ValidationOutcome rumble,
    ValidationOutcome paddles) =>
    new(
        "Elite Test Pad",
        "gameinput:primary",
        "usb",
        "Test OS",
        "Test Runtime",
        new ControllerCapabilities(true, true, true, true, true),
        [
            new(ValidationReport.StandardControlsCheckId, "Standard controls", standard),
            new(ValidationReport.ReconnectCheckId, "Disconnect and reconnect", reconnect),
            new(ValidationReport.RumbleCheckId, "Distinct rumble cues", rumble),
            new(ValidationReport.PaddlesCheckId, "Four independent paddles", paddles),
        ],
        "paddle notes",
        "rumble notes",
        string.Empty,
        DateTimeOffset.UtcNow);

static async Task TestMockSessionNavigationAsync()
{
    await using var adapter = new MockAgentAdapter();
    await adapter.StartAsync().ConfigureAwait(false);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    await using var enumerator = adapter.ReadEventsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected the ready event.");
    AssertEqual("mock-1", enumerator.Current.SessionId);

    await adapter.ExecuteAsync(new AgentCommand(AgentCommandKind.NextSession)).ConfigureAwait(false);
    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected a switch event.");
    AssertEqual("mock-2", enumerator.Current.SessionId);

    await adapter.ExecuteAsync(new AgentCommand(AgentCommandKind.PreviousSession)).ConfigureAwait(false);
    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected a switch-back event.");
    AssertEqual("mock-1", enumerator.Current.SessionId);

    // Previous at the first session stays put instead of going negative.
    await adapter.ExecuteAsync(new AgentCommand(AgentCommandKind.PreviousSession)).ConfigureAwait(false);
    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected a boundary event.");
    AssertEqual("mock-1", enumerator.Current.SessionId);
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

internal sealed class ScriptedController : IControllerDevice
{
    private readonly Channel<ControllerInputEvent> _events = Channel.CreateUnbounded<ControllerInputEvent>();

    public string Id => "scripted";

    public string DisplayName => "Scripted test controller";

    public ControllerCapabilities Capabilities { get; } = new(
        HasFourPaddles: true,
        HasLowFrequencyRumble: true,
        HasHighFrequencyRumble: true,
        HasLeftTriggerRumble: true,
        HasRightTriggerRumble: true);

    public bool IsConnected => true;

    public void Emit(ControllerControl control, ControllerInputEventKind kind) =>
        _events.Writer.TryWrite(new ControllerInputEvent(
            Id,
            control,
            kind,
            kind == ControllerInputEventKind.Pressed ? 1f : 0f,
            DateTimeOffset.UtcNow));

    public async IAsyncEnumerable<ControllerInputEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var inputEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return inputEvent;
        }
    }

    public ValueTask PlayAsync(HapticPattern pattern, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopHapticsAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class SingleControllerProvider(ScriptedController controller) : IControllerProvider
{
    private bool _handedOut;

    public ValueTask<IControllerDevice?> GetPrimaryControllerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_handedOut)
        {
            return ValueTask.FromResult<IControllerDevice?>(null);
        }

        _handedOut = true;
        return ValueTask.FromResult<IControllerDevice?>(controller);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
