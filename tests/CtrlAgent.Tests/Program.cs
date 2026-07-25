using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CtrlAgent.Adapters.ClaudeCode;
using CtrlAgent.Adapters.Mock;
using CtrlAgent.Controllers.DualSense;
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
    ("Axis threshold latches until the axis drops", TestAxisThresholdLatchAsync),
    ("Profile JSON round-trips", TestProfileJsonRoundTripAsync),
    ("Unsafe or ambiguous profiles are rejected", TestProfileValidationAsync),
    ("Profile layers activate by device capability", TestProfileLayersAsync),
    ("Haptic hub survives detach and device loss", TestHapticHubAsync),
    ("Validation report computes go/no-go gates", TestValidationReportGatesAsync),
    ("Validation report renders evidence markdown", TestValidationReportMarkdownAsync),
    ("Claude stream parser classifies protocol messages", TestClaudeStreamParserAsync),
    ("Executable resolver probes PATH and PATHEXT", TestExecutableResolverAsync),
    ("Claude permission responses carry session rules", TestClaudePermissionResponseAsync),
    ("DualSense protocol parses input and builds output", TestDualSenseProtocolAsync),
    ("Host engine runs press-to-approval loop end to end", TestHostEngineEndToEndAsync),
    ("Captured input passes only approval commands", TestInputCaptureFilterAsync),
    ("Host engine queues prompts while the agent is busy", TestPromptQueueAsync),
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

static Task TestAxisThresholdLatchAsync()
{
    var profile = new ControllerProfile(
        "axis",
        [
            new(ControllerControl.RightTrigger, InputGesture.AxisThreshold, AgentCommandKind.Interrupt),
        ]);
    var engine = new MappingEngine(profile);
    var start = DateTimeOffset.UtcNow;

    ControllerInputEvent Axis(float value, int ms) => new(
        "test-controller",
        ControllerControl.RightTrigger,
        ControllerInputEventKind.ValueChanged,
        value,
        start + TimeSpan.FromMilliseconds(ms));

    AssertEqual(1, engine.Process(Axis(0.6f, 0)).Count);

    // Jitter above the threshold must not re-fire.
    AssertEqual(0, engine.Process(Axis(0.7f, 10)).Count);
    AssertEqual(0, engine.Process(Axis(0.55f, 20)).Count);

    // Dropping below re-arms; the next crossing fires again.
    AssertEqual(0, engine.Process(Axis(0.2f, 30)).Count);
    AssertEqual(1, engine.Process(Axis(0.9f, 40)).Count);
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

static Task TestProfileLayersAsync()
{
    var layered = new ControllerProfile(
        "layered",
        [
            new(
                ControllerControl.PaddleLeft1,
                InputGesture.Press,
                AgentCommandKind.ApproveOnce,
                RequiresPendingApproval: true,
                Layer: "paddles"),
            new(
                ControllerControl.A,
                InputGesture.Press,
                AgentCommandKind.ApproveOnce,
                new HashSet<ControllerControl> { ControllerControl.RightShoulder },
                RequiresPendingApproval: true,
                Layer: "fallback"),
            new(ControllerControl.B, InputGesture.Press, AgentCommandKind.Interrupt),
        ],
        [
            new ProfileLayer("paddles", LayerActivation.RequiresPaddles),
            new ProfileLayer("fallback", LayerActivation.WithoutPaddles),
        ]);

    AssertEqual(0, ControllerProfileValidator.Validate(layered).Count);

    // Layers and memberships survive the JSON round-trip.
    var roundTripped = ControllerProfileJson.Deserialize(ControllerProfileJson.Serialize(layered));
    AssertEqual(2, roundTripped.Layers!.Count);
    AssertEqual(LayerActivation.RequiresPaddles, roundTripped.Layers[0].Activation);
    AssertEqual("paddles", roundTripped.Bindings[0].Layer);

    // A paddle-equipped device activates the paddle layer and mutes the fallback chord.
    var withPaddles = new MappingEngine(layered);
    withPaddles.SetPendingApproval("session", "request");
    withPaddles.SetDeviceCapabilities(new ControllerCapabilities(true, true, true, true, true));
    AssertEqual(1, withPaddles.Process(Press(ControllerControl.PaddleLeft1)).Count);
    _ = withPaddles.Process(Press(ControllerControl.RightShoulder));
    AssertEqual(0, withPaddles.Process(Press(ControllerControl.A)).Count);

    // A paddle-less device mutes the paddle layer and activates the fallback chord.
    var withoutPaddles = new MappingEngine(layered);
    withoutPaddles.SetPendingApproval("session", "request");
    withoutPaddles.SetDeviceCapabilities(new ControllerCapabilities(false, true, true, false, false));
    AssertEqual(0, withoutPaddles.Process(Press(ControllerControl.PaddleLeft1)).Count);
    _ = withoutPaddles.Process(Press(ControllerControl.RightShoulder));
    var chord = withoutPaddles.Process(Press(ControllerControl.A));
    AssertEqual(1, chord.Count);
    AssertEqual(AgentCommandKind.ApproveOnce, chord[0].Kind);

    // Co-active layers still collide; a base binding overlaps every layer.
    var colliding = new ControllerProfile(
        "colliding",
        [
            new(ControllerControl.A, InputGesture.Press, AgentCommandKind.SubmitPrompt),
            new(ControllerControl.A, InputGesture.Press, AgentCommandKind.Interrupt, Layer: "paddles"),
        ],
        [new ProfileLayer("paddles", LayerActivation.RequiresPaddles)]);
    Assert(ControllerProfileValidator.Validate(colliding).Count > 0, "Expected base/layer collision to be rejected.");

    // Referencing an undeclared layer is an error.
    var dangling = new ControllerProfile(
        "dangling",
        [new(ControllerControl.A, InputGesture.Press, AgentCommandKind.SubmitPrompt, Layer: "ghost")]);
    Assert(ControllerProfileValidator.Validate(dangling).Count > 0, "Expected undefined layer reference to be rejected.");
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

static Task TestInputCaptureFilterAsync()
{
    // While a fullscreen controller UI owns the input, only the approval
    // family may reach the agent — everything else navigates the menu.
    Assert(HostEngine.IsAllowedWhileCaptured(AgentCommandKind.ApproveOnce), "ApproveOnce must bypass capture.");
    Assert(HostEngine.IsAllowedWhileCaptured(AgentCommandKind.ApproveForSession), "ApproveForSession must bypass capture.");
    Assert(HostEngine.IsAllowedWhileCaptured(AgentCommandKind.Decline), "Decline must bypass capture.");
    Assert(HostEngine.IsAllowedWhileCaptured(AgentCommandKind.Cancel), "Cancel must bypass capture.");
    Assert(!HostEngine.IsAllowedWhileCaptured(AgentCommandKind.SubmitPrompt), "SubmitPrompt must be captured.");
    Assert(!HostEngine.IsAllowedWhileCaptured(AgentCommandKind.Interrupt), "Interrupt must be captured.");
    Assert(!HostEngine.IsAllowedWhileCaptured(AgentCommandKind.NewSession), "NewSession must be captured.");
    Assert(!HostEngine.IsAllowedWhileCaptured(AgentCommandKind.ReviewChanges), "ReviewChanges must be captured.");
    return Task.CompletedTask;
}

static async Task TestPromptQueueAsync()
{
    var controller = new ScriptedController();
    var provider = new SingleControllerProvider(controller);
    var adapter = new MockAgentAdapter();
    var engine = new HostEngine(
        provider,
        adapter,
        ControllerProfile.Default,
        new HostEngineOptions("default prompt"));

    var queueCounts = new List<int>();
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    engine.PromptQueueChanged += count =>
    {
        lock (queueCounts)
        {
            queueCounts.Add(count);
        }
    };
    engine.AgentEventReceived += agentEvent =>
    {
        if (agentEvent.State == AgentStateKind.Idle)
        {
            ready.TrySetResult();
        }

        if (agentEvent.State == AgentStateKind.Working && agentEvent.Message == "second prompt")
        {
            secondStarted.TrySetResult();
        }
    };

    await engine.StartAsync().ConfigureAwait(false);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

    // Wait for the adapter's initial Idle so the busy flag starts settled.
    await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

    await engine.SubmitPromptAsync("first prompt").ConfigureAwait(false);
    await engine.SubmitPromptAsync("second prompt").ConfigureAwait(false);

    // The second prompt must wait for the first turn, then send itself.
    await secondStarted.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

    lock (queueCounts)
    {
        Assert(queueCounts.Contains(1), "Expected the second prompt to be queued.");
        Assert(queueCounts.Contains(0), "Expected the queue to drain after the first turn.");
    }

    await engine.DisposeAsync().ConfigureAwait(false);
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
    var init = ParseClaudeLine(
        """{"type":"system","subtype":"init","cwd":"/repo","session_id":"sess-1","model":"claude-sonnet-5","tools":[]}""");
    Assert(
        init is ClaudeStreamMessage.SessionInit { SessionId: "sess-1", Model: "claude-sonnet-5" },
        "Expected SessionInit with model.");

    var text = ParseClaudeLine(
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Hello there"}]},"session_id":"sess-1"}""");
    Assert(text is ClaudeStreamMessage.AssistantActivity { Summary: "Hello there" }, "Expected assistant text summary.");

    var tool = ParseClaudeLine(
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]},"session_id":"sess-1"}""");
    Assert(tool is ClaudeStreamMessage.AssistantActivity { Summary: "Bash: ls" }, "Expected concrete tool detail.");

    var todos = ParseClaudeLine(
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"TodoWrite","input":{"todos":[{"content":"a","status":"completed"},{"content":"fix tests","activeForm":"Fixing tests","status":"in_progress"},{"content":"c","status":"pending"}]}}]},"session_id":"sess-1"}""");
    Assert(
        todos is ClaudeStreamMessage.AssistantActivity { Summary: "Plan 1/3 — Fixing tests" },
        "Expected todo progress summary.");

    var delta = ParseClaudeLine(
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"chunk"}},"session_id":"sess-1"}""");
    Assert(delta is ClaudeStreamMessage.TextDelta { Text: "chunk" }, "Expected streamed text delta.");

    var thinking = ParseClaudeLine(
        """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking"}},"session_id":"sess-1"}""");
    Assert(thinking is ClaudeStreamMessage.ThinkingStarted, "Expected thinking start.");

    var earlyTool = ParseClaudeLine(
        """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","name":"Edit","input":{"file_path":"src/App.cs"}}},"session_id":"sess-1"}""");
    Assert(
        earlyTool is ClaudeStreamMessage.AssistantActivity { Summary: "Edit: src/App.cs" },
        "Expected early tool detail from the stream event.");

    var success = ParseClaudeLine(
        """{"type":"result","subtype":"success","is_error":false,"result":"All done","duration_ms":42500,"num_turns":3,"total_cost_usd":0.1845,"session_id":"sess-1"}""");
    Assert(
        success is ClaudeStreamMessage.TurnResult { IsError: false, Summary: "All done (42.5s · 3 turns · $0.1845)" },
        "Expected success result with turn stats.");

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

    var interrupt = JsonDocument.Parse(JsonSerializer.Serialize(
        ClaudeControlRequest.Interrupt("ctrl_1"))).RootElement;
    AssertEqual("control_request", interrupt.GetProperty("type").GetString());
    AssertEqual("interrupt", interrupt.GetProperty("request").GetProperty("subtype").GetString());

    var mode = JsonDocument.Parse(JsonSerializer.Serialize(
        ClaudeControlRequest.SetPermissionMode("ctrl_2", "plan"))).RootElement;
    AssertEqual("control_request", mode.GetProperty("type").GetString());
    AssertEqual("ctrl_2", mode.GetProperty("request_id").GetString());
    AssertEqual("set_permission_mode", mode.GetProperty("request").GetProperty("subtype").GetString());
    AssertEqual("plan", mode.GetProperty("request").GetProperty("mode").GetString());
    return Task.CompletedTask;
}

static Task TestDualSenseProtocolAsync()
{
    // USB report 0x01: sticks centered-ish, R2 pulled, Cross + D-pad-left +
    // R1 + Options held.
    var usb = new byte[64];
    usb[0] = 0x01;
    usb[1] = 128;                    // LX
    usb[2] = 128;                    // LY
    usb[3] = 200;                    // RX
    usb[4] = 55;                     // RY
    usb[5] = 0;                      // L2
    usb[6] = 255;                    // R2
    usb[8] = 0x20 | 0x06;            // Cross + hat 6 (west)
    usb[9] = 0x02 | 0x20;            // R1 + Options
    Assert(DualSenseProtocol.TryParseInput(usb, out var state), "USB report must parse.");
    Assert(state.Buttons.HasFlag(DualSenseButtons.Cross), "Cross must be pressed.");
    Assert(state.Buttons.HasFlag(DualSenseButtons.DPadLeft), "Hat west must decode to D-pad left.");
    Assert(state.Buttons.HasFlag(DualSenseButtons.R1), "R1 must be pressed.");
    Assert(state.Buttons.HasFlag(DualSenseButtons.Options), "Options must be pressed.");
    Assert(!state.Buttons.HasFlag(DualSenseButtons.Triangle), "Triangle must not be pressed.");
    AssertEqual((byte)255, state.RightTrigger);
    AssertEqual((byte)200, state.RightStickX);

    // Edge paddles live in the third button byte.
    var edge = new byte[64];
    edge[0] = 0x01;
    edge[8] = 0x08;                  // hat released
    edge[10] = 0x40 | 0x20;          // LeftPaddle + RightFunction
    Assert(DualSenseProtocol.TryParseInput(edge, out var edgeState), "Edge report must parse.");
    Assert(edgeState.Buttons.HasFlag(DualSenseButtons.LeftPaddle), "Left paddle must be pressed.");
    Assert(edgeState.Buttons.HasFlag(DualSenseButtons.RightFunction), "Right Fn must be pressed.");
    Assert(!edgeState.Buttons.HasFlag(DualSenseButtons.DPadUp), "Released hat must press nothing.");

    // Bluetooth report 0x31 carries the same payload shifted by one byte.
    var bluetooth = new byte[78];
    bluetooth[0] = 0x31;
    bluetooth[2] = 128;
    bluetooth[9] = 0x28;             // Cross + hat 8 (released)
    Assert(DualSenseProtocol.TryParseInput(bluetooth, out var btState), "Bluetooth report must parse.");
    Assert(btState.Buttons.HasFlag(DualSenseButtons.Cross), "Cross must be pressed over Bluetooth.");

    // USB output: id, flags, motors, and the cyan lightbar in place.
    var output = DualSenseProtocol.BuildUsbOutput(1f, 0.5f, 0x00, 0xD4, 0xFF);
    AssertEqual(DualSenseProtocol.UsbOutputReportLength, output.Length);
    AssertEqual((byte)0x02, output[0]);
    AssertEqual((byte)0x03, output[1]);
    AssertEqual((byte)128, output[3]);   // high-frequency → right motor
    AssertEqual((byte)255, output[4]);   // low-frequency → left motor
    AssertEqual((byte)0xD4, output[46]);
    AssertEqual((byte)0xFF, output[47]);

    // Bluetooth output: correct frame plus a self-consistent trailing CRC32.
    var btOutput = DualSenseProtocol.BuildBluetoothOutput(3, 0.25f, 0f, 0x00, 0xD4, 0xFF);
    AssertEqual(DualSenseProtocol.BluetoothOutputReportLength, btOutput.Length);
    AssertEqual((byte)0x31, btOutput[0]);
    AssertEqual((byte)0x30, btOutput[1]);
    var expectedCrc = DualSenseProtocol.ComputeOutputCrc(btOutput.AsSpan(0, 74));
    var actualCrc = btOutput[74] | ((uint)btOutput[75] << 8) | ((uint)btOutput[76] << 16) | ((uint)btOutput[77] << 24);
    AssertEqual(expectedCrc, actualCrc);

    Assert(
        DualSenseProtocol.IsSupported(DualSenseProtocol.SonyVendorId, DualSenseProtocol.DualSenseEdgeProductId),
        "Edge PID must be recognized.");
    Assert(!DualSenseProtocol.IsSupported(0x045E, 0x02FF), "Non-Sony devices must be rejected.");
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

static Task TestExecutableResolverAsync()
{
    var windowsPathExt = ".COM;.EXE;.BAT;.CMD;.PS1";
    var binDir = "bin";
    var npmDir = "npm";
    var searchPath = binDir + Path.PathSeparator + npmDir;

    // npm installs provide only a .cmd shim; the resolver must find it.
    var shim = Path.Combine(npmDir, "claude.cmd");
    AssertEqual(shim, AgentExecutableResolver.Resolve(
        "claude", searchPath, windowsPathExt, path => path == shim));

    // Within one directory, PATHEXT order applies: .exe beats .cmd.
    var exe = Path.Combine(npmDir, "claude.exe");
    AssertEqual(exe, AgentExecutableResolver.Resolve(
        "claude", searchPath, windowsPathExt, path => path == shim || path == exe));

    // Across directories, PATH order wins even when a later dir has an .exe.
    var earlyShim = Path.Combine(binDir, "claude.cmd");
    AssertEqual(earlyShim, AgentExecutableResolver.Resolve(
        "claude", searchPath, windowsPathExt, path => path == earlyShim || path == exe));

    // .ps1 shims are never selected: CreateProcess cannot launch them.
    var ps1 = Path.Combine(npmDir, "claude.ps1");
    AssertEqual("claude", AgentExecutableResolver.Resolve(
        "claude", searchPath, windowsPathExt, path => path == ps1));

    // Explicit paths pass through untouched.
    var explicitPath = Path.Combine("tools", "claude.cmd");
    AssertEqual(explicitPath, AgentExecutableResolver.Resolve(
        explicitPath, searchPath, windowsPathExt, _ => false));

    // Without PATHEXT (non-Windows), the bare name is probed as-is.
    var unixBinary = Path.Combine(binDir, "claude");
    AssertEqual(unixBinary, AgentExecutableResolver.Resolve(
        "claude", searchPath, null, path => path == unixBinary));

    // Unresolvable names fall back to the original for a natural failure.
    AssertEqual("claude", AgentExecutableResolver.Resolve(
        "claude", searchPath, windowsPathExt, _ => false));

    return Task.CompletedTask;
}

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
