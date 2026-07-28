using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CtrlAgent.Adapters.ClaudeCode;
using CtrlAgent.Adapters.Codex;
using CtrlAgent.Adapters.Mock;
using CtrlAgent.Controllers.DualSense;
using CtrlAgent.Core;
using CtrlAgent.Hosting;
using CtrlAgent.Presentation;

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
    ("Stick up and stick down are separate bindings", TestDirectionalAxisAsync),
    ("Profile JSON round-trips", TestProfileJsonRoundTripAsync),
    ("Guide control binds and round-trips", TestGuideControlAsync),
    ("Session-setting cycles wrap and resolve", TestSessionSettingCyclesAsync),
    ("Permission modes exclude the unusable one", TestPermissionModesAsync),
    ("Unsafe or ambiguous profiles are rejected", TestProfileValidationAsync),
    ("Profile layers activate by device capability", TestProfileLayersAsync),
    ("Reachable bindings exclude controls the device lacks", TestReachableBindingsAsync),
    ("Guide bindings hide on transports that cannot send it", TestGuideReachabilityAsync),
    ("Attachments ride along with the next prompt", TestPromptComposerAsync),
    ("Host-handled commands never reach the adapter", TestHostHandledCommandsAsync),
    ("Haptic hub survives detach and device loss", TestHapticHubAsync),
    ("Validation report computes go/no-go gates", TestValidationReportGatesAsync),
    ("Validation report renders evidence markdown", TestValidationReportMarkdownAsync),
    ("Claude stream parser classifies protocol messages", TestClaudeStreamParserAsync),
    ("Executable resolver probes PATH and PATHEXT", TestExecutableResolverAsync),
    ("Claude permission responses carry session rules", TestClaudePermissionResponseAsync),
    ("Codex protocol parser classifies app-server traffic", TestCodexProtocolParserAsync),
    ("Codex turn status maps to the right agent state", TestCodexTurnStatusAsync),
    ("DualSense protocol parses input and builds output", TestDualSenseProtocolAsync),
    ("Host engine runs press-to-approval loop end to end", TestHostEngineEndToEndAsync),
    ("Captured input passes only approval commands", TestInputCaptureFilterAsync),
    ("Host engine queues prompts while the agent is busy", TestPromptQueueAsync),
    ("Host engine swaps profiles at runtime with validation", TestHostEngineProfileSwapAsync),
    ("Host engine tracks session settings from every route", TestSessionSettingsTrackingAsync),
    ("Mock adapter emits approval lifecycle", TestMockAdapterAsync),
    ("Mock adapter navigates sessions", TestMockSessionNavigationAsync),
    ("Mock adapter resumes a session by id", TestMockResumeSessionAsync),
    ("Claude session catalog lists the on-disk store", TestClaudeSessionCatalogAsync),
    ("MarkdownLite parses prose, code, and lists", TestMarkdownLiteAsync),
    ("An interrupt feels the same on every adapter", TestInterruptIsUniformAsync),
    ("Transcript folds streaming prose into one bubble", TestTranscriptFoldingAsync),
    ("Agent states read as English, not enum names", TestAgentStateTextAsync),
    ("Model names shorten to chip size", TestModelTextAsync),
    ("Log lines classify most-severe first", TestLogClassificationAsync),
    ("Approval highlight covers chord modifiers", TestApprovalControlsAsync),
    ("Unified diff parses edits, adds, renames, binaries", TestUnifiedDiffParserAsync),
    ("Workspace diff folds tracked and untracked changes", TestWorkspaceDiffAsync),
    ("Startup preflight names every missing prerequisite", TestStartupPreflightAsync),
    ("Stored transcripts reload as prose, tools skipped", TestTranscriptReloadAsync),
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

// Up and down on one stick used to be inexpressible: both sides of the
// threshold check compared Math.Abs, so a single binding fired in both
// directions and a scroll-up/scroll-down pair could not exist. A negative
// threshold now means the negative half of the axis.
static Task TestDirectionalAxisAsync()
{
    var profile = new ControllerProfile(
        "stick",
        [
            new(ControllerControl.RightThumbstickY, InputGesture.AxisThreshold,
                AgentCommandKind.ScrollOutputUp, MinimumValue: 0.6f),
            new(ControllerControl.RightThumbstickY, InputGesture.AxisThreshold,
                AgentCommandKind.ScrollOutputDown, MinimumValue: -0.6f),
        ]);

    var engine = new MappingEngine(profile);
    var at = DateTimeOffset.UnixEpoch;

    AgentCommand? Move(float value)
    {
        at = at.AddMilliseconds(50);
        return engine.Process(new ControllerInputEvent(
            "test", ControllerControl.RightThumbstickY,
            ControllerInputEventKind.ValueChanged, value, at)).SingleOrDefault();
    }

    // Pushing up fires only the up command.
    AssertEqual(AgentCommandKind.ScrollOutputUp, Move(0.9f)!.Kind);
    // Still held: latched, so jitter does not repeat it.
    Assert(Move(0.95f) is null, "A held axis must not re-fire.");
    // Back to centre, then down: only the down command.
    Assert(Move(0f) is null, "Returning to centre fires nothing.");
    AssertEqual(AgentCommandKind.ScrollOutputDown, Move(-0.9f)!.Kind);
    Assert(Move(-0.95f) is null, "A held axis must not re-fire in either direction.");

    // Crossing straight from one extreme to the other still fires the new one.
    AssertEqual(AgentCommandKind.ScrollOutputUp, Move(0.8f)!.Kind);

    // Both directions on one stick are a legal profile, not a collision.
    Assert(ControllerProfileValidator.Validate(profile).Count == 0, "Opposite directions must validate.");

    // Same direction twice is still a collision.
    var clashing = new ControllerProfile(
        "clash",
        [
            new(ControllerControl.RightThumbstickY, InputGesture.AxisThreshold,
                AgentCommandKind.ScrollOutputUp, MinimumValue: 0.6f),
            new(ControllerControl.RightThumbstickY, InputGesture.AxisThreshold,
                AgentCommandKind.ScrollOutputDown, MinimumValue: 0.8f),
        ]);
    Assert(ControllerProfileValidator.Validate(clashing).Count > 0, "Same direction twice must collide.");

    // A zero threshold would fire at rest.
    var atRest = new ControllerProfile(
        "zero",
        [new(ControllerControl.RightThumbstickY, InputGesture.AxisThreshold,
             AgentCommandKind.ScrollOutputUp, MinimumValue: 0f)]);
    Assert(ControllerProfileValidator.Validate(atRest).Count > 0, "A zero threshold must be rejected.");
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

// The Xbox/PS button is a normal control everywhere above the transport
// layer: it must parse from JSON, survive a round-trip, and map like any
// other button. Whether a given transport can actually report it is a
// device concern, not a mapping one.
static Task TestGuideControlAsync()
{
    var profile = new ControllerProfile(
        "guide",
        [
            new(ControllerControl.Guide, InputGesture.Press, AgentCommandKind.ReviewChanges),
        ]);

    var json = ControllerProfileJson.Serialize(profile);
    Assert(json.Contains("guide", StringComparison.OrdinalIgnoreCase), "Guide must serialize by name.");

    var restored = ControllerProfileJson.Deserialize(json);
    AssertEqual(ControllerControl.Guide, restored.Bindings.Single().Control);

    var engine = new MappingEngine(restored);
    var commands = engine.Process(new ControllerInputEvent(
        "test", ControllerControl.Guide, ControllerInputEventKind.Pressed, 1f, DateTimeOffset.UnixEpoch));

    AssertEqual(1, commands.Count);
    AssertEqual(AgentCommandKind.ReviewChanges, commands[0].Kind);
    return Task.CompletedTask;
}

// Cycle position lives in the host, not the adapters, so the wrap logic is
// worth pinning down: an unknown current value must start the cycle rather
// than throw or stall.
static Task TestSessionSettingCyclesAsync()
{
    AssertEqual("plan", AgentModes.Next(AgentModes.PermissionModes, "default"));
    AssertEqual("low", AgentModes.Next(AgentModes.EffortCycle, "max"));      // wraps
    AssertEqual("default", AgentModes.Next(AgentModes.ModelCycle, null));     // unset
    AssertEqual("default", AgentModes.Next(AgentModes.ModelCycle, "nonsense"));
    AssertEqual("opus", AgentModes.Next(AgentModes.ModelCycle, "sonnet"));

    // Case-insensitive, because mode names arrive from JSON profiles too.
    AssertEqual("opus", AgentModes.Next(AgentModes.ModelCycle, "SONNET"));

    // The idle inputs are now bound, and the profile still validates.
    var profile = ControllerProfile.Default;
    AssertEqual(0, ControllerProfileValidator.Validate(profile).Count);
    foreach (var control in new[]
             {
                 ControllerControl.DPadUp,
                 ControllerControl.DPadDown,
                 ControllerControl.LeftThumbstickButton,
                 ControllerControl.RightThumbstickButton,
             })
    {
        Assert(
            profile.Bindings.Any(binding => binding.Control == control),
            $"{control} should now carry a session control.");
    }

    return Task.CompletedTask;
}

// bypassPermissions is listed by the CLI but rejected on a live session
// unless it was launched with --dangerously-skip-permissions, so cycling
// into it only ever produced an error. It is also the one mode that would
// switch off the approval prompts this tool exists to surface.
static Task TestPermissionModesAsync()
{
    Assert(
        !AgentModes.PermissionModes.Contains("bypassPermissions", StringComparer.OrdinalIgnoreCase),
        "bypassPermissions must not be reachable from the mode cycle.");
    Assert(AgentModes.PermissionModes.Contains("default"), "default must be in the cycle.");
    Assert(AgentModes.PermissionModes.Contains("plan"), "plan must be in the cycle.");

    // The cycle must return to its start rather than dead-ending.
    var mode = AgentModes.PermissionModes[0];
    for (var step = 0; step < AgentModes.PermissionModes.Count; step++)
    {
        mode = AgentModes.Next(AgentModes.PermissionModes, mode);
    }

    AssertEqual(AgentModes.PermissionModes[0], mode);
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

static Task TestReachableBindingsAsync()
{
    var paddles = new ControllerCapabilities(true, true, true, true, true);
    var noPaddles = new ControllerCapabilities(false, true, true, false, false);
    var profile = ControllerProfile.Default;

    // The default profile is unlayered and lists paddle bindings ahead of
    // their chord fallbacks, so "first match wins" must still not hand a
    // paddle to hardware that has none — this is what the UI coaches from.
    var firstApprove = (ControllerCapabilities? capabilities) => profile
        .ReachableBindings(capabilities)
        .First(binding => binding.Command == AgentCommandKind.ApproveOnce);

    AssertEqual(ControllerControl.PaddleLeft1, firstApprove(paddles).Control);

    var fallback = firstApprove(noPaddles);
    AssertEqual(ControllerControl.A, fallback.Control);
    Assert(
        fallback.Modifiers is not null && fallback.Modifiers.Contains(ControllerControl.RightShoulder),
        "Expected the paddle-less approve hint to be the RB+A chord.");

    // Unknown capabilities stay optimistic, matching MappingEngine's layer rule.
    AssertEqual(ControllerControl.PaddleLeft1, firstApprove(null).Control);

    // Every paddle binding drops out, and nothing else does.
    var reachable = profile.ReachableBindings(noPaddles).ToList();
    Assert(
        reachable.All(binding => binding.Control is not (ControllerControl.PaddleLeft1
            or ControllerControl.PaddleLeft2
            or ControllerControl.PaddleRight1
            or ControllerControl.PaddleRight2)),
        "Expected no paddle bindings on a paddle-less device.");
    AssertEqual(profile.Bindings.Count - 4, reachable.Count);
    AssertEqual(profile.Bindings.Count, profile.ReachableBindings(paddles).Count());

    // A chord whose modifier is a paddle is unreachable even though its
    // primary control exists.
    var paddleModified = new ControllerProfile(
        "paddle-modified",
        [
            new(
                ControllerControl.A,
                InputGesture.Press,
                AgentCommandKind.ApproveOnce,
                new HashSet<ControllerControl> { ControllerControl.PaddleLeft1 },
                RequiresPendingApproval: true),
        ]);
    AssertEqual(0, paddleModified.ReachableBindings(noPaddles).Count());
    AssertEqual(1, paddleModified.ReachableBindings(paddles).Count());

    // Layer activation still applies alongside the control check.
    var layered = new ControllerProfile(
        "layered",
        [new(ControllerControl.B, InputGesture.Press, AgentCommandKind.Interrupt, Layer: "paddles")],
        [new ProfileLayer("paddles", LayerActivation.RequiresPaddles)]);
    AssertEqual(0, layered.ReachableBindings(noPaddles).Count());
    AssertEqual(1, layered.ReachableBindings(paddles).Count());
    return Task.CompletedTask;
}

static Task TestGuideReachabilityAsync()
{
    var profile = new ControllerProfile(
        "guide",
        [
            new(ControllerControl.A, InputGesture.Press, AgentCommandKind.SubmitPrompt),
            new(ControllerControl.Guide, InputGesture.Press, AgentCommandKind.ReviewChanges),
        ]);

    var withGuide = new ControllerCapabilities(false, true, true, false, false, HasGuideButton: true);
    var withoutGuide = new ControllerCapabilities(false, true, true, false, false, HasGuideButton: false);

    AssertEqual(2, profile.ReachableBindings(withGuide).Count());
    AssertEqual(1, profile.ReachableBindings(withoutGuide).Count());
    Assert(
        profile.ReachableBindings(withoutGuide).All(b => b.Control != ControllerControl.Guide),
        "A transport without the Guide button must not advertise Guide bindings.");

    // Unknown hardware keeps everything visible: hiding a binding on a guess
    // is worse than listing one that might not fire.
    AssertEqual(2, profile.ReachableBindings(null).Count());
    Assert(
        new ControllerCapabilities(false, true, true, false, false).HasGuideButton,
        "The capability must default to available.");

    // A chord whose modifier is Guide is unreachable for the same reason.
    var chord = new ControllerProfile(
        "chord",
        [new(ControllerControl.A, InputGesture.Press, AgentCommandKind.SubmitPrompt,
             Modifiers: new HashSet<ControllerControl> { ControllerControl.Guide })]);
    AssertEqual(0, chord.ReachableBindings(withoutGuide).Count());
    AssertEqual(1, chord.ReachableBindings(withGuide).Count());
    return Task.CompletedTask;
}

static Task TestPromptComposerAsync()
{
    // No attachments: the prompt is untouched.
    AssertEqual("fix the tests", PromptComposer.Compose("fix the tests", []));

    // The ask comes first, then the files — the agent should read what to do
    // before the bookkeeping.
    var one = PromptComposer.Compose("review this", ["/repo/a.cs"]);
    Assert(one.StartsWith("review this", StringComparison.Ordinal), "The instruction leads.");
    Assert(one.Contains("Attached file:", StringComparison.Ordinal), "Singular for one file.");
    Assert(one.Contains("- /repo/a.cs", StringComparison.Ordinal), "The path is listed.");

    var many = PromptComposer.Compose("compare", ["/repo/a.cs", "/repo/b.cs"]);
    Assert(many.Contains("Attached files:", StringComparison.Ordinal), "Plural for several.");
    Assert(many.Contains("- /repo/b.cs", StringComparison.Ordinal), "Every path is listed.");

    // Attaching without typing still sends something meaningful rather than a
    // prompt that begins with a blank line.
    var bare = PromptComposer.Compose("   ", ["/repo/a.cs"]);
    Assert(bare.StartsWith("Attached file:", StringComparison.Ordinal), "No leading blank.");
    return Task.CompletedTask;
}

// StartVoicePrompt and AttachFile are bindable so a controller can reach them,
// but they are UI actions — an adapter has no idea what a microphone is, and
// forwarding them would surface as "Unsupported command" errors.
static async Task TestHostHandledCommandsAsync()
{
    var recording = new RecordingAdapter();
    await using var engine = new HostEngine(
        new SingleControllerProvider(new ScriptedController()),
        recording,
        ControllerProfile.Default,
        new HostEngineOptions("prompt"));

    var voice = 0;
    var attach = 0;
    engine.VoicePromptRequested += () => voice++;
    engine.AttachFileRequested += () => attach++;

    await engine.StartVoicePromptAsync().ConfigureAwait(false);
    await engine.AttachFileAsync().ConfigureAwait(false);

    AssertEqual(1, voice);
    AssertEqual(1, attach);
    AssertEqual(0, recording.Commands.Count);

    // An ordinary command still gets through, so the interception is targeted.
    await engine.InterruptAsync().ConfigureAwait(false);
    AssertEqual(1, recording.Commands.Count);
    AssertEqual(AgentCommandKind.Interrupt, recording.Commands[0].Kind);

    // Both are in the default profile, so a stock controller can reach them.
    Assert(
        ControllerProfile.Default.Bindings.Any(b => b.Command == AgentCommandKind.StartVoicePrompt),
        "The default profile must bind voice.");
    Assert(
        ControllerProfile.Default.Bindings.Any(b => b.Command == AgentCommandKind.AttachFile),
        "The default profile must bind attach.");
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

        if (agentEvent.State == AgentStateKind.Working &&
            agentEvent.Message?.Contains("second prompt", StringComparison.Ordinal) == true)
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

// The GUI labels model/effort/mode from engine.Settings. Before this, the
// permission mode was the one knob the engine did not track: a GUI kept a
// private cycle index, so a mode set from a controller binding moved the
// session without moving the label, and the two disagreed from then on.
static async Task TestSessionSettingsTrackingAsync()
{
    var controller = new ScriptedController();
    await using var engine = new HostEngine(
        new SingleControllerProvider(controller),
        new MockAgentAdapter(),
        ControllerProfile.Default,
        new HostEngineOptions("prompt"));

    var published = new List<SessionSettings>();
    engine.SessionSettingsChanged += settings => published.Add(settings);

    // Unset until asked: the agent starts on its own defaults.
    Assert(engine.Settings.Model is null, "Model must start unset.");
    Assert(engine.Settings.PermissionMode is null, "Permission mode must start unset.");

    await engine.SetPermissionModeAsync("plan").ConfigureAwait(false);
    AssertEqual("plan", engine.Settings.PermissionMode!);

    await engine.CycleModelAsync().ConfigureAwait(false);
    AssertEqual("default", engine.Settings.Model!);
    await engine.CycleModelAsync().ConfigureAwait(false);
    AssertEqual("sonnet", engine.Settings.Model!);

    await engine.CycleEffortAsync().ConfigureAwait(false);
    AssertEqual("low", engine.Settings.Effort!);

    // One knob moving must not clear the others.
    AssertEqual("plan", engine.Settings.PermissionMode!);
    AssertEqual("sonnet", engine.Settings.Model!);
    AssertEqual(4, published.Count);
    AssertEqual("sonnet", published[^1].Model!);

    // An adapter that reports the live model on its events (Claude's init
    // does, every turn) updates Settings without the user touching a knob —
    // and a repeat of the same model does not re-publish.
    var reporting = new ModelReportingAdapter();
    var reportingController = new ScriptedController();
    await using var reportingEngine = new HostEngine(
        new SingleControllerProvider(reportingController),
        reporting,
        ControllerProfile.Default,
        new HostEngineOptions("prompt"));

    var reported = new List<SessionSettings>();
    var firstReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    reportingEngine.SessionSettingsChanged += settings =>
    {
        reported.Add(settings);
        firstReport.TrySetResult();
    };

    await reportingEngine.StartAsync().ConfigureAwait(false);
    reporting.Emit(new AgentEvent(
        "reporting", "sess-1", AgentStateKind.Idle, DateTimeOffset.UtcNow,
        "ready", Model: "claude-opus-5"));
    await firstReport.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    AssertEqual("claude-opus-5", reportingEngine.Settings.Model!);

    reporting.Emit(new AgentEvent(
        "reporting", "sess-1", AgentStateKind.Working, DateTimeOffset.UtcNow,
        "working", Model: "claude-opus-5"));
    reporting.Emit(new AgentEvent(
        "reporting", "sess-1", AgentStateKind.Completed, DateTimeOffset.UtcNow,
        "done", Model: "claude-opus-5"));
    await Task.Delay(150).ConfigureAwait(false);
    AssertEqual(1, reported.Count);
}

// The Codex classification used to live inside the adapter, entangled with
// the process and the event channel, so none of it could be exercised without
// spawning a real app-server. These are the shapes the wire actually sends.
static Task TestCodexProtocolParserAsync()
{
    // A server request expecting an answer: id present alongside method.
    var approval = ParseCodex("""
        {"method":"item/tool/requestApproval","id":42,
         "params":{"threadId":"th-1","turnId":"tn-9","command":"rm -rf build"}}
        """);
    var request = AssertIs<CodexMessage.UserActionRequired>(approval);
    AssertEqual("42", request.RequestId);           // raw text, so a numeric id stays numeric
    AssertEqual("th-1", request.ThreadId!);
    AssertEqual("tn-9", request.TurnId!);
    AssertEqual("rm -rf build", request.Message);
    AssertEqual(AgentStateKind.ApprovalRequired, request.State);

    // Free-form input blocks the turn too, but it is not an approval — the
    // controller's approve/decline bindings must not claim to answer it.
    var input = ParseCodex("""
        {"method":"item/tool/requestUserInput","id":"abc","params":{"reason":"Which branch?"}}
        """);
    var inputRequest = AssertIs<CodexMessage.UserActionRequired>(input);
    AssertEqual(AgentStateKind.WaitingForInput, inputRequest.State);
    AssertEqual("Which branch?", inputRequest.Message);
    AssertEqual("\"abc\"", inputRequest.RequestId);   // string ids keep their quotes

    // "reason" wins over "command", and the method name is the last resort.
    var bare = AssertIs<CodexMessage.UserActionRequired>(
        ParseCodex("""{"method":"item/tool/requestApproval","id":1,"params":{}}"""));
    AssertEqual("item/tool/requestApproval", bare.Message);

    // Notifications: no id.
    var started = AssertIs<CodexMessage.ThreadStarted>(
        ParseCodex("""{"method":"thread/started","params":{"thread":{"id":"th-7"}}}"""));
    AssertEqual("th-7", started.ThreadId!);

    // A malformed thread/started is still a thread/started; the adapter simply
    // has no id to remember.
    Assert(
        AssertIs<CodexMessage.ThreadStarted>(ParseCodex("""{"method":"thread/started"}""")).ThreadId is null,
        "A thread/started without an id must parse with a null id.");

    var turn = AssertIs<CodexMessage.TurnStarted>(
        ParseCodex("""{"method":"turn/started","params":{"threadId":"th-1","turn":{"id":"tn-2"}}}"""));
    AssertEqual("tn-2", turn.TurnId!);

    var resolved = AssertIs<CodexMessage.ServerRequestResolved>(
        ParseCodex("""{"method":"serverRequest/resolved","params":{"requestId":42}}"""));
    AssertEqual("42", resolved.RequestId);

    // Responses to our own requests: no method, id correlates the reply.
    var ok = AssertIs<CodexMessage.ResponseReceived>(
        ParseCodex("""{"id":7,"result":{"thread":{"id":"th-3"}}}"""));
    AssertEqual(7L, ok.Id);
    Assert(ok.Error is null, "A result response must not carry an error.");
    AssertEqual("th-3", ok.Result.GetProperty("thread").GetProperty("id").GetString()!);

    var failed = AssertIs<CodexMessage.ResponseReceived>(
        ParseCodex("""{"id":8,"error":{"code":-32000,"message":"nope"}}"""));
    Assert(failed.Error is not null && failed.Error.Contains("nope", StringComparison.Ordinal),
        "An error response must carry the raw error JSON.");

    // A missing result is an empty object, not a crash: callers index into it.
    AssertEqual(
        JsonValueKind.Object,
        AssertIs<CodexMessage.ResponseReceived>(ParseCodex("""{"id":9}""")).Result.ValueKind);

    // Anything unrecognized is inert rather than an error event.
    AssertIs<CodexMessage.Ignored>(ParseCodex("""{"method":"item/started","params":{}}"""));
    AssertIs<CodexMessage.Ignored>(ParseCodex("""{"jsonrpc":"2.0"}"""));
    return Task.CompletedTask;
}

// Turn status drives which rumble fires, so the mapping is worth pinning.
static Task TestCodexTurnStatusAsync()
{
    AssertEqual(AgentStateKind.Completed, TurnState("""{"turn":{"status":"completed"}}"""));
    AssertEqual(AgentStateKind.Error, TurnState("""{"turn":{"status":"failed"}}"""));

    // A deliberate stop reports as AgentInterrupt.State, identically across
    // every adapter, so the same button never means two different things.
    AssertEqual(AgentInterrupt.State, TurnState("""{"turn":{"status":"interrupted"}}"""));
    AssertEqual(AgentInterrupt.State, TurnState("""{"turn":{"status":"INTERRUPTED"}}"""));
    Assert(AgentInterrupt.State != AgentStateKind.Error, "An interrupt must never read as an error.");
    AssertEqual(
        AgentInterrupt.Message,
        AssertIs<CodexMessage.TurnFinished>(
            ParseCodex("""{"method":"turn/completed","params":{"turn":{"status":"interrupted"}}}""")).Summary);

    // An absent status is treated as a normal completion.
    AssertEqual(AgentStateKind.Completed, TurnState("""{"turn":{}}"""));

    // A failure message replaces the generic summary; without one the status
    // still names itself.
    var withError = AssertIs<CodexMessage.TurnFinished>(ParseCodex(
        """{"method":"turn/completed","params":{"turn":{"status":"failed","error":{"message":"sandbox denied"}}}}"""));
    AssertEqual("sandbox denied", withError.Summary);
    AssertEqual(
        "Codex turn completed.",
        AssertIs<CodexMessage.TurnFinished>(
            ParseCodex("""{"method":"turn/completed","params":{"turn":{"status":"completed"}}}""")).Summary);
    return Task.CompletedTask;

    static AgentStateKind TurnState(string turnParams) =>
        AssertIs<CodexMessage.TurnFinished>(
            ParseCodex($$"""{"method":"turn/completed","params":{{turnParams}}}""")).State;
}

static CodexMessage ParseCodex(string json)
{
    using var document = JsonDocument.Parse(json);
    return CodexProtocolParser.Parse(document.RootElement);
}

static T AssertIs<T>(CodexMessage message)
    where T : CodexMessage
{
    Assert(message is T, $"Expected {typeof(T).Name} but got {message.GetType().Name}.");
    return (T)message;
}

// The same button must not mean two different things in the hand. Claude
// reported an interrupt as Completed while Codex and the mock reported Idle,
// which routes to no pattern at all — so interrupting produced a confirming
// buzz on one agent and silence on another.
static async Task TestInterruptIsUniformAsync()
{
    // The shared answer must produce a real cue. Idle deliberately routes to
    // null, which is exactly the trap this invariant exists to avoid.
    var router = new FeedbackRouter();
    var pattern = router.Route(new AgentEvent(
        "test", "session", AgentInterrupt.State, DateTimeOffset.UtcNow, AgentInterrupt.Message));
    Assert(pattern is not null, "An interrupt must route to a haptic pattern, not silence.");

    // The mock is the adapter a test can drive end to end; it must publish the
    // shared state rather than its own idea of one.
    await using var adapter = new MockAgentAdapter();
    await adapter.StartAsync().ConfigureAwait(false);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var events = adapter.ReadEventsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

    await adapter.ExecuteAsync(new AgentCommand(AgentCommandKind.SubmitPrompt, Text: "work"), timeout.Token)
        .ConfigureAwait(false);
    await adapter.ExecuteAsync(new AgentCommand(AgentCommandKind.Interrupt), timeout.Token)
        .ConfigureAwait(false);

    var sawInterrupt = false;
    while (await events.MoveNextAsync().ConfigureAwait(false))
    {
        if (events.Current.Message == AgentInterrupt.Message)
        {
            AssertEqual(AgentInterrupt.State, events.Current.State);
            sawInterrupt = true;
            break;
        }
    }

    Assert(sawInterrupt, "The mock adapter must publish the shared interrupt event.");
}

static AgentEvent Event(AgentStateKind state, string message) =>
    new("test", "session", state, DateTimeOffset.UtcNow, message);

// The rule that matters: streamed prose updates ONE bubble, and anything that
// is not prose closes it so the next chunk starts a fresh one. Getting this
// wrong either erases text the user was reading or shatters a reply into a
// wall of fragments.
static Task TestAgentStateTextAsync()
{
    AssertEqual("approval", AgentStateText.Describe(AgentStateKind.ApprovalRequired));
    AssertEqual("waiting", AgentStateText.Describe(AgentStateKind.WaitingForInput));
    AssertEqual("working", AgentStateText.Describe(AgentStateKind.Working));
    AssertEqual("done", AgentStateText.Describe(AgentStateKind.Completed));

    // Every state must have wording, and none may leak a C# identifier or run
    // long enough to reflow the command bar.
    foreach (var state in Enum.GetValues<AgentStateKind>())
    {
        var text = AgentStateText.Describe(state);
        Assert(text.Length is > 0 and <= 10, $"{state} reads as '{text}', which is too long.");
        Assert(text != state.ToString(), $"{state} still shows its enum name.");
    }

    return Task.CompletedTask;
}

static Task TestModelTextAsync()
{
    // Full CLI-reported ids shed only the vendor prefix every entry shares.
    AssertEqual("opus-4-7[1m]", ModelText.Short("claude-opus-4-7[1m]"));
    AssertEqual("sonnet-5", ModelText.Short("claude-sonnet-5"));

    // User-picked aliases and unknown names pass through untouched.
    AssertEqual("opus", ModelText.Short("opus"));
    AssertEqual("gpt-x", ModelText.Short("gpt-x"));

    // Not reported yet reads as the CLI's own default, and the degenerate
    // bare prefix is not shortened into an empty chip.
    AssertEqual("default", ModelText.Short(null));
    AssertEqual("default", ModelText.Short("  "));
    AssertEqual("claude-", ModelText.Short("claude-"));
    return Task.CompletedTask;
}

static Task TestTranscriptFoldingAsync()
{
    var folder = new TranscriptFolder();

    AssertIsAction<TranscriptAction.StartBubble>(folder.Fold(Event(AgentStateKind.Working, "Let me")));
    var update = AssertIsAction<TranscriptAction.UpdateBubble>(
        folder.Fold(Event(AgentStateKind.Working, "Let me check")));
    AssertEqual("Let me check", update.Text);
    Assert(folder.IsStreaming, "Prose must leave the bubble open.");

    // A tool call is activity: it closes the bubble...
    var tool = AssertIsAction<TranscriptAction.AddActivity>(
        folder.Fold(Event(AgentStateKind.Working, "Bash: dotnet test")));
    AssertEqual("Bash: dotnet test", tool.Text);
    Assert(!folder.IsStreaming, "Activity must close the open bubble.");

    // ...so the prose after it starts a new one rather than overwriting.
    AssertIsAction<TranscriptAction.StartBubble>(folder.Fold(Event(AgentStateKind.Working, "Tests pass")));

    // An approval does NOT close the bubble: it interrupts a sentence the
    // agent will finish once answered.
    var locked = AssertIsAction<TranscriptAction.AddActivity>(
        folder.Fold(Event(AgentStateKind.ApprovalRequired, "wants: Write: a.cs")));
    Assert(locked.Text.StartsWith("🔒", StringComparison.Ordinal), "Approvals are marked.");
    Assert(folder.IsStreaming, "An approval must not close the bubble.");
    AssertIsAction<TranscriptAction.UpdateBubble>(folder.Fold(Event(AgentStateKind.Working, "Tests pass, done")));

    // Results close it and are marked by outcome.
    Assert(
        AssertIsAction<TranscriptAction.AddActivity>(
            folder.Fold(Event(AgentStateKind.Completed, "done"))).Text.StartsWith("✓", StringComparison.Ordinal),
        "Completion is ticked.");
    Assert(
        AssertIsAction<TranscriptAction.AddActivity>(
            folder.Fold(Event(AgentStateKind.Error, "boom"))).Text.StartsWith("✕", StringComparison.Ordinal),
        "Errors are crossed.");

    // Empty messages contribute nothing and must not open a bubble.
    AssertIsAction<TranscriptAction.None>(folder.Fold(Event(AgentStateKind.Working, "   ")));

    // Reset is what a workspace swap uses; the next prose must start fresh.
    folder.Fold(Event(AgentStateKind.Working, "streaming"));
    folder.Reset();
    Assert(!folder.IsStreaming, "Reset must close the bubble.");
    AssertIsAction<TranscriptAction.StartBubble>(folder.Fold(Event(AgentStateKind.Working, "new session")));
    return Task.CompletedTask;
}

// Order is the rule: "Approval declined" contains both an approval word and an
// error word, and it has to read as an error.
static Task TestLogClassificationAsync()
{
    AssertEqual(LogSeverity.Error, LogClassifier.Classify("Approval declined by the user"));
    AssertEqual(LogSeverity.Error, LogClassifier.Classify("Agent session failed to start"));
    AssertEqual(LogSeverity.Approval, LogClassifier.Classify("Approval required: Write"));
    AssertEqual(LogSeverity.Success, LogClassifier.Classify("Controller connected"));
    AssertEqual(LogSeverity.Agent, LogClassifier.Classify("Turn started"));
    AssertEqual(LogSeverity.Normal, LogClassifier.Classify("Rumble preview"));

    Assert(LogClassifier.IsControllerEvent("[controller] A pressed"), "Controller lines are taggable.");
    Assert(!LogClassifier.IsControllerEvent("[agent] working"), "Agent lines are not controller input.");
    return Task.CompletedTask;
}

// Highlighting only the A of "RB+A" points the user at the one button that
// will not work on its own.
static Task TestApprovalControlsAsync()
{
    var controls = ApprovalControls.From(ControllerProfile.Default);
    Assert(controls.Count > 0, "The default profile must expose approval controls.");

    foreach (var binding in ControllerProfile.Default.Bindings.Where(b => b.RequiresPendingApproval))
    {
        Assert(controls.Contains(binding.Control), $"{binding.Control} must be highlighted.");
        if (binding.Modifiers is { Count: > 0 } modifiers)
        {
            foreach (var modifier in modifiers)
            {
                Assert(controls.Contains(modifier), $"Modifier {modifier} must be highlighted too.");
            }
        }
    }

    // Bindings that are not approval-gated must not light up.
    var plain = new ControllerProfile(
        "plain", [new(ControllerControl.A, InputGesture.Press, AgentCommandKind.SubmitPrompt)]);
    AssertEqual(0, ApprovalControls.From(plain).Count);
    return Task.CompletedTask;
}

static T AssertIsAction<T>(TranscriptAction action)
    where T : TranscriptAction
{
    Assert(action is T, $"Expected {typeof(T).Name} but got {action.GetType().Name}.");
    return (T)action;
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
        """{"type":"system","subtype":"init","cwd":"/repo","session_id":"sess-1","model":"claude-sonnet-5","tools":[],"slash_commands":["compact","review"],"mcp_servers":[{"name":"a","status":"connected"},{"name":"b","status":"failed"}]}""");
    Assert(
        init is ClaudeStreamMessage.SessionInit { SessionId: "sess-1", Model: "claude-sonnet-5" },
        "Expected SessionInit with model.");
    var initMessage = (ClaudeStreamMessage.SessionInit)init;
    AssertEqual(2, initMessage.SlashCommands.Count);
    AssertEqual("/compact", initMessage.SlashCommands[0]);
    AssertEqual("2 MCP servers, 1 failed", initMessage.McpSummary);

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
        success is ClaudeStreamMessage.TurnResult { IsError: false, Summary: "All done (42.5s · 3 turns · ~$0.1845 API rate)" },
        "Expected success result with turn stats.");

    var failure = ParseClaudeLine(
        """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"sess-1"}""");
    Assert(failure is ClaudeStreamMessage.TurnResult { IsError: true }, "Expected error result.");

    var toolResult = ParseClaudeLine(
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"3 files changed"}]},"session_id":"sess-1"}""");
    Assert(
        toolResult is ClaudeStreamMessage.ToolResultReceived { Summary: "3 files changed", IsError: false },
        "Expected tool result snippet.");

    var toolError = ParseClaudeLine(
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","is_error":true,"content":[{"type":"text","text":"command not found"}]}]},"session_id":"sess-1"}""");
    Assert(
        toolError is ClaudeStreamMessage.ToolResultReceived { Summary: "command not found", IsError: true },
        "Expected tool error snippet from array content.");

    var permission = ParseClaudeLine(
        """{"type":"control_request","request_id":"perm-1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm x"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"rm *"}],"behavior":"allow","destination":"session"}]}}""");
    Assert(
        permission is ClaudeStreamMessage.PermissionRequest { RequestId: "perm-1", ToolName: "Bash" },
        "Expected permission request.");
    var request = (ClaudeStreamMessage.PermissionRequest)permission;
    AssertEqual("rm x", request.Input.GetProperty("command").GetString());
    Assert(request.Suggestions is not null, "Expected permission suggestions to be captured.");
    AssertEqual("Bash: rm x", ClaudeStreamParser.DescribeToolUse(request.ToolName, request.Input));

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

    // When the CLI suggested rules, approve-for-session echoes them verbatim.
    using var suggestionsDocument = JsonDocument.Parse(
        """[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test"}],"behavior":"allow","destination":"projectSettings"}]""");
    var echoed = JsonDocument.Parse(JsonSerializer.Serialize(
        ClaudePermissionResponse.Allow(
            "req-4", "Bash", input, forSession: true, suggestionsDocument.RootElement.Clone()))).RootElement;
    var echoedRule = echoed.GetProperty("response").GetProperty("response").GetProperty("updatedPermissions")[0];
    AssertEqual("npm test", echoedRule.GetProperty("rules")[0].GetProperty("ruleContent").GetString());
    AssertEqual("projectSettings", echoedRule.GetProperty("destination").GetString());

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
    AssertEqual((byte)0x0F, output[1]);  // rumble + haptics + both trigger-effect flags
    AssertEqual((byte)128, output[3]);   // high-frequency → right motor
    AssertEqual((byte)255, output[4]);   // low-frequency → left motor
    AssertEqual((byte)0x00, output[11]); // no effect requested → mode 0 clears
    AssertEqual((byte)0x00, output[22]);
    AssertEqual((byte)0xD4, output[46]);
    AssertEqual((byte)0xFF, output[47]);

    // Adaptive triggers: right block at payload 10..12, left at 21..23.
    var resisting = DualSenseProtocol.BuildUsbOutput(
        0f, 0f, 0x00, 0xD4, 0xFF,
        DualSenseTriggerEffect.Resistance(1f),
        DualSenseTriggerEffect.Resistance(0.5f));
    AssertEqual((byte)0x01, resisting[11]); // right: continuous resistance
    AssertEqual((byte)0x20, resisting[12]);
    AssertEqual((byte)128, resisting[13]);
    AssertEqual((byte)0x01, resisting[22]); // left
    AssertEqual((byte)0x20, resisting[23]);
    AssertEqual((byte)255, resisting[24]);
    Assert(
        DualSenseTriggerEffect.Resistance(0f) == DualSenseTriggerEffect.Off,
        "Zero strength must clear the trigger effect.");

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

static async Task TestMockResumeSessionAsync()
{
    await using var adapter = new MockAgentAdapter();
    await adapter.StartAsync().ConfigureAwait(false);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    await using var enumerator = adapter.ReadEventsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected the ready event.");

    await adapter.ExecuteAsync(new AgentCommand(AgentCommandKind.NextSession)).ConfigureAwait(false);
    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected a switch event.");
    AssertEqual("mock-2", enumerator.Current.SessionId);

    // A direct resume jumps to the named session — the sidebar's path, not
    // the blind cycle.
    await adapter.ExecuteAsync(new AgentCommand(AgentCommandKind.ResumeSession, Text: "mock-1")).ConfigureAwait(false);
    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected a resume event.");
    AssertEqual("mock-1", enumerator.Current.SessionId);
    AssertEqual(AgentStateKind.Idle, enumerator.Current.State);

    // A bad id is an error, not a silent no-op.
    await adapter.ExecuteAsync(new AgentCommand(AgentCommandKind.ResumeSession, Text: "nonsense")).ConfigureAwait(false);
    Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Expected an error event.");
    AssertEqual(AgentStateKind.Error, enumerator.Current.State);
}

static async Task TestClaudeSessionCatalogAsync()
{
    // The store's directory name: every character outside [A-Za-z0-9] → '-'.
    AssertEqual(
        "G--Old-Files-repo-name",
        ClaudeSessionCatalog.EncodeProjectDirectoryName(@"G:\Old Files\repo_name"));

    // Titles: a summary line wins over the first prompt; synthetic
    // angle-bracket user entries never become titles; junk lines are skipped.
    AssertEqual(
        "Fix the login bug",
        ClaudeSessionCatalog.ExtractTitle(
        [
            "not json at all",
            """{"type":"queue-operation","operation":"enqueue"}""",
            """{"type":"user","message":{"role":"user","content":"<system-reminder>ignore me</system-reminder>"}}""",
            """{"type":"user","message":{"role":"user","content":"Fix the login bug"}}""",
        ]));
    AssertEqual(
        "Login bug investigation",
        ClaudeSessionCatalog.ExtractTitle(
        [
            """{"type":"user","message":{"role":"user","content":"Fix the login bug"}}""",
            """{"type":"summary","summary":"Login bug investigation"}""",
        ]));
    AssertEqual(
        "From a block array",
        ClaudeSessionCatalog.ExtractTitle(
        [
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"From a block array"}]}}""",
        ]));

    // Listing a real (temp) store: newest activity first, session id from the
    // file name, a transcript with no usable prompt still lists.
    var home = Path.Combine(Path.GetTempPath(), $"ctrlagent-test-{Guid.NewGuid():N}");
    var workspace = @"C:\repos\demo";
    var store = Path.Combine(home, "projects", ClaudeSessionCatalog.EncodeProjectDirectoryName(workspace));
    Directory.CreateDirectory(store);
    try
    {
        File.WriteAllText(
            Path.Combine(store, "older.jsonl"),
            """{"type":"user","message":{"role":"user","content":"First session prompt"}}""" + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(store, "newer.jsonl"),
            """{"type":"file-history-snapshot","messageId":"x"}""" + Environment.NewLine);
        File.SetLastWriteTimeUtc(Path.Combine(store, "older.jsonl"), DateTime.UtcNow.AddHours(-2));

        var sessions = ClaudeSessionCatalog.ListSessions(workspace, home);
        AssertEqual(2, sessions.Count);
        AssertEqual("newer", sessions[0].SessionId);
        AssertEqual("New session", sessions[0].Title);
        AssertEqual("older", sessions[1].SessionId);
        AssertEqual("First session prompt", sessions[1].Title);

        AssertEqual(0, ClaudeSessionCatalog.ListSessions(@"C:\repos\absent", home).Count);
    }
    finally
    {
        DeleteTempDirectory(home);
    }

    await Task.CompletedTask;
}

static async Task TestMarkdownLiteAsync()
{
    // Inline styling inside one paragraph.
    var blocks = MarkdownLite.Parse("**bold** and `code` here");
    AssertEqual(1, blocks.Count);
    var paragraph = (MarkdownParagraph)blocks[0];
    AssertEqual(4, paragraph.Runs.Count);
    AssertEqual("bold", paragraph.Runs[0].Text);
    Assert(paragraph.Runs[0].Bold, "First run should be bold.");
    AssertEqual(" and ", paragraph.Runs[1].Text);
    Assert(paragraph.Runs[2].Code, "Third run should be code.");
    AssertEqual("code", paragraph.Runs[2].Text);
    AssertEqual(" here", paragraph.Runs[3].Text);

    // Bare asterisks with spaces around them are arithmetic, not emphasis.
    var literal = (MarkdownParagraph)MarkdownLite.Parse("2 * 3 * 4")[0];
    AssertEqual(1, literal.Runs.Count);
    AssertEqual("2 * 3 * 4", literal.Runs[0].Text);

    // Fenced code keeps its body verbatim and carries the language.
    blocks = MarkdownLite.Parse("intro\n```csharp\nvar x = 1;\n```\noutro");
    AssertEqual(3, blocks.Count);
    var code = (MarkdownCodeBlock)blocks[1];
    AssertEqual("var x = 1;", code.Code);
    AssertEqual("csharp", code.Language);

    // Bullets, numbered items, and headings become their own blocks.
    blocks = MarkdownLite.Parse("## Plan\n- first\n2. second");
    AssertEqual(3, blocks.Count);
    AssertEqual(2, ((MarkdownHeading)blocks[0]).Level);
    AssertEqual("•", ((MarkdownListItem)blocks[1]).Marker);
    AssertEqual("2.", ((MarkdownListItem)blocks[2]).Marker);

    // An unterminated fence still renders as code — streams cut mid-block.
    blocks = MarkdownLite.Parse("```\nhalf a block");
    AssertEqual(1, blocks.Count);
    AssertEqual("half a block", ((MarkdownCodeBlock)blocks[0]).Code);

    await Task.CompletedTask;
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

static Task TestUnifiedDiffParserAsync()
{
    // A modified file with two hunks: counts and line kinds, prefixes kept.
    var files = UnifiedDiffParser.Parse(
        """
        diff --git a/src/App.cs b/src/App.cs
        index 1111111..2222222 100644
        --- a/src/App.cs
        +++ b/src/App.cs
        @@ -1,3 +1,3 @@
         using System;
        -var x = 1;
        +var x = 2;
        @@ -10,2 +10,3 @@ void Main()
         return;
        +Log();
        \ No newline at end of file
        """);
    AssertEqual(1, files.Count);
    AssertEqual("src/App.cs", files[0].Path);
    AssertEqual(DiffFileChange.Modified, files[0].Change);
    AssertEqual(2, files[0].Additions);
    AssertEqual(1, files[0].Deletions);
    AssertEqual(DiffLineKind.HunkHeader, files[0].Lines[0].Kind);
    AssertEqual(DiffLineKind.Context, files[0].Lines[1].Kind);
    AssertEqual("-var x = 1;", files[0].Lines[2].Text);
    AssertEqual("+var x = 2;", files[0].Lines[3].Text);
    Assert(files[0].Lines.All(line => line.Text != @"\ No newline at end of file"),
        "The no-newline marker is noise and must not render.");
    AssertEqual("M src/App.cs · +2 −1", files[0].Headline);

    // New, deleted, renamed, and binary files all classify from the header
    // block — and the +++/--- paths win over the diff --git guess.
    files = UnifiedDiffParser.Parse(
        """
        diff --git a/new.txt b/new.txt
        new file mode 100644
        --- /dev/null
        +++ b/new.txt
        @@ -0,0 +1,1 @@
        +hello
        diff --git a/gone.txt b/gone.txt
        deleted file mode 100644
        --- a/gone.txt
        +++ /dev/null
        @@ -1,1 +0,0 @@
        -bye
        diff --git a/old-name.cs b/new-name.cs
        similarity index 97%
        rename from old-name.cs
        rename to new-name.cs
        diff --git a/logo.png b/logo.png
        index 3333333..4444444 100644
        Binary files a/logo.png and b/logo.png differ
        """);
    AssertEqual(4, files.Count);
    AssertEqual(DiffFileChange.Added, files[0].Change);
    AssertEqual("new.txt", files[0].Path);
    AssertEqual(1, files[0].Additions);
    AssertEqual(DiffFileChange.Deleted, files[1].Change);
    AssertEqual("gone.txt", files[1].Path);
    AssertEqual(1, files[1].Deletions);
    AssertEqual(DiffFileChange.Renamed, files[2].Change);
    AssertEqual("new-name.cs", files[2].Path);
    AssertEqual("old-name.cs", files[2].RenamedFrom);

    // Spaces make git quote the whole header, which defeats the header-path
    // guess entirely — only the rename lines carry the clean paths.
    var quoted = UnifiedDiffParser.Parse(
        """
        diff --git "a/my file.cs" "b/my file 2.cs"
        similarity index 100%
        rename from my file.cs
        rename to my file 2.cs
        """);
    AssertEqual("my file 2.cs", quoted[0].Path);
    AssertEqual("my file.cs", quoted[0].RenamedFrom);
    Assert(files[3].IsBinary, "Binary marker must classify the file as binary.");
    AssertEqual(0, files[3].Lines.Count);
    AssertEqual("A new.txt · +1 −0", files[0].Headline);
    AssertEqual("R old-name.cs → new-name.cs · +0 −0", files[2].Headline);
    AssertEqual("M logo.png · binary", files[3].Headline);

    // Junk in, nothing out — not an exception, not a phantom file.
    AssertEqual(0, UnifiedDiffParser.Parse(null).Count);
    AssertEqual(0, UnifiedDiffParser.Parse("not a diff at all").Count);

    // The summary line reads like a status chip.
    AssertEqual("Working tree clean", new WorkspaceChanges([]).Summary);
    AssertEqual("Unavailable", new WorkspaceChanges([], Error: "no git").Summary);
    var one = new WorkspaceChanges([new DiffFile("a.txt", DiffFileChange.Modified, false, 3, 1, [])]);
    AssertEqual("1 file · +3 −1", one.Summary);
    return Task.CompletedTask;
}

static async Task TestWorkspaceDiffAsync()
{
    var repo = Path.Combine(Path.GetTempPath(), $"ctrlagent-diff-{Guid.NewGuid():N}");
    Directory.CreateDirectory(repo);
    try
    {
        // A directory that is not a repository reports why, not an empty diff.
        var outside = await WorkspaceDiff.CollectAsync(repo);
        Assert(outside.IsError, "A non-repository must surface an error.");

        await RunAsync("git", ["-C", repo, "init", "--quiet"]);
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "one\ntwo\n");
        await RunAsync("git", ["-C", repo, "add", "."]);
        await RunAsync("git", [
            "-C", repo, "-c", "user.email=t@t", "-c", "user.name=t",
            "commit", "--quiet", "-m", "seed"]);

        // Clean tree: no files, no error.
        var clean = await WorkspaceDiff.CollectAsync(repo);
        Assert(!clean.IsError, $"Clean repo errored: {clean.Error}");
        AssertEqual(0, clean.Files.Count);

        // One tracked edit + one untracked file: both appear, the untracked
        // one as an all-added synthetic diff.
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "one\nTWO\n");
        File.WriteAllText(Path.Combine(repo, "fresh.txt"), "alpha\nbeta\n");
        File.WriteAllBytes(Path.Combine(repo, "blob.bin"), [0x50, 0x00, 0x4E, 0x47]);
        var changes = await WorkspaceDiff.CollectAsync(repo);
        Assert(!changes.IsError, $"Collect errored: {changes.Error}");
        AssertEqual(3, changes.Files.Count);
        var tracked = changes.Files.Single(file => file.Path == "tracked.txt");
        AssertEqual(1, tracked.Additions);
        AssertEqual(1, tracked.Deletions);
        var fresh = changes.Files.Single(file => file.Path == "fresh.txt");
        AssertEqual(DiffFileChange.Added, fresh.Change);
        AssertEqual(2, fresh.Additions);
        AssertEqual("+alpha", fresh.Lines[0].Text);
        var blob = changes.Files.Single(file => file.Path == "blob.bin");
        Assert(blob.IsBinary, "A null byte marks an untracked file as binary.");
        AssertEqual(0, blob.Lines.Count);
        AssertEqual("3 files · +3 −1", changes.Summary);
    }
    finally
    {
        DeleteTempDirectory(repo);
    }

    static async Task RunAsync(string fileName, string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo { FileName = fileName };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException($"{fileName} did not start.");
        await process.WaitForExitAsync();
        Assert(process.ExitCode == 0, $"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}.");
    }
}

static Task TestTranscriptReloadAsync()
{
    // A realistic stored session: prompts, replies with tool machinery mixed
    // into the content blocks, synthetic user entries, junk lines.
    var entries = ClaudeSessionCatalog.ParseTranscript(
    [
        """{"type":"file-history-snapshot","messageId":"x"}""",
        """{"type":"user","message":{"role":"user","content":"Fix the login bug"}}""",
        """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Found it — the token check."},{"type":"tool_use","name":"Bash","input":{}}]}}""",
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"exit 0"}]}}""",
        """{"type":"user","message":{"role":"user","content":"<system-reminder>noise</system-reminder>"}}""",
        "not json at all",
        """{"type":"assistant","message":{"content":[{"type":"text","text":"Fixed."},{"type":"text","text":"Tests pass."},{"type":"tool_use","name":"Write","text":"raw dump"}]}}""",
        """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Ship it"}]}}""",
    ]);
    AssertEqual(4, entries.Count);
    Assert(entries[0].IsUser, "First entry is the user's prompt.");
    AssertEqual("Fix the login bug", entries[0].Text);
    Assert(!entries[1].IsUser, "Second entry is the assistant.");
    AssertEqual("Found it — the token check.", entries[1].Text);
    AssertEqual("Fixed.\n\nTests pass.", entries[2].Text);
    AssertEqual("Ship it", entries[3].Text);

    // The cap keeps the newest messages, not the oldest.
    var capped = ClaudeSessionCatalog.ParseTranscript(
    [
        """{"type":"user","message":{"role":"user","content":"first"}}""",
        """{"type":"user","message":{"role":"user","content":"second"}}""",
        """{"type":"user","message":{"role":"user","content":"third"}}""",
    ], maxEntries: 2);
    AssertEqual(2, capped.Count);
    AssertEqual("second", capped[0].Text);
    AssertEqual("third", capped[1].Text);

    // End to end through a temp store: the file path, sharing mode, and a
    // missing session are the parts ParseTranscript cannot cover.
    var home = Path.Combine(Path.GetTempPath(), $"ctrlagent-hist-{Guid.NewGuid():N}");
    var workspace = @"C:\repos\demo";
    var store = Path.Combine(home, "projects", ClaudeSessionCatalog.EncodeProjectDirectoryName(workspace));
    Directory.CreateDirectory(store);
    try
    {
        File.WriteAllLines(
            Path.Combine(store, "abc.jsonl"),
            [
                """{"type":"user","message":{"role":"user","content":"Hello"}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi."}]}}""",
            ]);

        var loaded = ClaudeSessionCatalog.LoadTranscript(workspace, "abc", home);
        AssertEqual(2, loaded.Count);
        AssertEqual("Hi.", loaded[1].Text);
        AssertEqual(0, ClaudeSessionCatalog.LoadTranscript(workspace, "missing", home).Count);
    }
    finally
    {
        Directory.Delete(home, recursive: true);
    }

    return Task.CompletedTask;
}

static Task TestStartupPreflightAsync()
{
    var repo = "repo";
    var shim = Path.Combine("npm", "claude.cmd");
    var pathExt = ".COM;.EXE;.BAT;.CMD";

    // Everything present: no problems, in both PATH styles.
    AssertEqual(0, StartupPreflight.Check(
        "claude", repo, null, null,
        directory => directory == repo, file => file == shim, "npm", pathExt).Count);
    var unixBinary = Path.Combine("bin", "claude");
    AssertEqual(0, StartupPreflight.Check(
        "claude", repo, null, null,
        directory => directory == repo, file => file == unixBinary, "bin", null).Count);

    // A deleted workspace names the path and says what to do.
    var problems = StartupPreflight.Check(
        "claude", @"C:\gone", null, null,
        _ => false, file => file == shim, "npm", pathExt);
    AssertEqual(1, problems.Count);
    Assert(problems[0].Contains(@"C:\gone"), "The workspace problem must name the path.");
    Assert(problems[0].Contains("Pick another folder"), "The workspace problem must say what to do.");

    // A missing CLI says how to install it — the whole point of preflighting.
    problems = StartupPreflight.Check(
        "claude", repo, null, null,
        directory => directory == repo, _ => false, "npm", pathExt);
    AssertEqual(1, problems.Count);
    Assert(problems[0].Contains("npm install -g @anthropic-ai/claude-code"),
        "The claude problem must carry the install command.");
    problems = StartupPreflight.Check(
        "codex", repo, null, null,
        directory => directory == repo, _ => false, "npm", pathExt);
    Assert(problems.Single().Contains("npm install -g @openai/codex"),
        "The codex problem must carry its own install command.");

    // An explicit executable path is checked directly, not through PATH.
    var explicitPath = Path.Combine("tools", "claude.cmd");
    problems = StartupPreflight.Check(
        "claude", repo, explicitPath, null,
        directory => directory == repo, _ => false, "npm", pathExt);
    Assert(problems.Single().Contains(explicitPath), "A missing explicit executable must be named.");
    AssertEqual(0, StartupPreflight.Check(
        "claude", repo, explicitPath, null,
        directory => directory == repo, file => file == explicitPath, "npm", pathExt).Count);

    // The mock agent spawns nothing, so an empty PATH is fine.
    AssertEqual(0, StartupPreflight.Check(
        "mock", repo, null, null,
        directory => directory == repo, _ => false, null, null).Count);

    // A missing profile file is its own problem, reported alongside others.
    problems = StartupPreflight.Check(
        "claude", repo, null, "missing.json",
        directory => directory == repo, file => file == shim, "npm", pathExt);
    Assert(problems.Single().Contains("missing.json"), "A missing profile must be named.");
    problems = StartupPreflight.Check(
        "claude", @"C:\gone", null, "missing.json",
        _ => false, _ => false, "npm", pathExt);
    AssertEqual(3, problems.Count);

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

/// <summary>
/// Removes a temp directory built by a test. Two things a plain
/// <see cref="Directory.Delete(string, bool)"/> gets wrong here: git marks
/// everything under <c>.git/objects</c> read-only and Windows refuses to
/// delete those, and a throwing cleanup replaces whichever assertion actually
/// failed. A leftover temp directory is the smaller problem, so this clears
/// the attribute first and then swallows whatever is left.
/// </summary>
static void DeleteTempDirectory(string path)
{
    if (!Directory.Exists(path))
    {
        return;
    }

    try
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(path, recursive: true);
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"  (left {path} behind: {exception.Message})");
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

/// <summary>An adapter whose events the test writes by hand — for exercising
/// what a host engine does with event metadata (the reported model).</summary>
internal sealed class ModelReportingAdapter : IAgentAdapter
{
    private readonly System.Threading.Channels.Channel<AgentEvent> _events =
        System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();

    public string Id => "reporting";

    public bool IsStarted { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        IsStarted = true;
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<AgentEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    public ValueTask ExecuteAsync(AgentCommand command, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public void Emit(AgentEvent agentEvent) => _events.Writer.TryWrite(agentEvent);

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
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


/// <summary>Records commands instead of doing anything with them.</summary>
internal sealed class RecordingAdapter : IAgentAdapter
{
    private readonly System.Threading.Channels.Channel<AgentEvent> _events =
        System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();

    public List<AgentCommand> Commands { get; } = [];

    public string Id => "recording";

    public bool IsStarted { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        IsStarted = true;
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<AgentEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    public ValueTask ExecuteAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        lock (Commands) Commands.Add(command);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
