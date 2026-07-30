using CtrlAgent.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Built-in focus modes preserve approvals and errors", TestCriticalEventsAsync),
    ("Deep Focus suppresses routine work and tools", TestDeepFocusAsync),
    ("Silent Watch suppresses completion but keeps interruption", TestSilentWatchAsync),
    ("Focus mode cycle wraps and publishes changes", TestFocusCycleAsync),
    ("Attention metrics count decisions and autonomous time", TestMetricsAsync),
    ("Focus intensity is clamped", TestIntensityAsync),
    ("Transient haptics resume the persistent layer", TestLayeredSchedulerAsync),
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

Console.WriteLine($"{tests.Length - failures}/{tests.Length} focus tests passed.");
return failures == 0 ? 0 : 1;

static Task TestCriticalEventsAsync()
{
    foreach (var mode in FocusContractSettings.Modes)
    {
        var contract = FocusContract.For(mode);
        Assert(contract.Allows(AttentionEventKind.ApprovalRequired), $"{mode} suppressed approval.");
        Assert(contract.Allows(AttentionEventKind.Error), $"{mode} suppressed error.");
    }
    return Task.CompletedTask;
}

static Task TestDeepFocusAsync()
{
    var previous = FocusContractSettings.Current;
    try
    {
        FocusContractSettings.Select(FocusMode.DeepFocus);
        var router = new FeedbackRouter();
        Assert(router.Route(Event(AgentStateKind.Working, "thinking")) is null, "Thinking should be quiet.");
        Assert(router.Route(Event(AgentStateKind.Working, "running tool")) is null, "Tool activity should be quiet.");
        AssertEqual("approval-required", router.Route(Event(AgentStateKind.ApprovalRequired))?.Name);
        AssertEqual("completed", router.Route(Event(AgentStateKind.Completed))?.Name);
    }
    finally
    {
        FocusContractSettings.Current = previous;
    }
    return Task.CompletedTask;
}

static Task TestSilentWatchAsync()
{
    var previous = FocusContractSettings.Current;
    try
    {
        FocusContractSettings.Select(FocusMode.SilentWatch);
        var router = new FeedbackRouter();
        Assert(router.Route(Event(AgentStateKind.Completed, "Finished.")) is null, "Ordinary completion should be quiet.");
        AssertEqual("interrupted", router.Route(Event(AgentStateKind.Completed, "Turn interrupted."))?.Name);
        AssertEqual("error", router.Route(Event(AgentStateKind.Error))?.Name);
    }
    finally
    {
        FocusContractSettings.Current = previous;
    }
    return Task.CompletedTask;
}

static Task TestFocusCycleAsync()
{
    var previous = FocusContractSettings.Current;
    var changes = 0;
    void Changed(FocusContract _) => changes++;
    FocusContractSettings.Changed += Changed;
    try
    {
        FocusContractSettings.Select(FocusMode.Accessibility);
        var next = FocusContractSettings.Next();
        AssertEqual(FocusMode.DeepFocus, next);
        AssertEqual(FocusMode.DeepFocus, FocusContractSettings.Current.Mode);
        Assert(changes >= 1, "Expected a focus-mode change event.");
    }
    finally
    {
        FocusContractSettings.Changed -= Changed;
        FocusContractSettings.Current = previous;
    }
    return Task.CompletedTask;
}

static Task TestMetricsAsync()
{
    var metrics = new AttentionMetrics();
    var start = DateTimeOffset.UtcNow;
    metrics.RecordDecision(AttentionEventKind.RoutineProgress, delivered: false);
    metrics.RecordDecision(AttentionEventKind.ApprovalRequired, delivered: true);
    metrics.RecordApprovalResponse();
    metrics.ObserveAgentState(AgentStateKind.Working, start);
    metrics.ObserveAgentState(AgentStateKind.Completed, start.AddMinutes(3));

    var snapshot = metrics.Snapshot(start.AddMinutes(3));
    AssertEqual(1L, snapshot.RoutineNotificationsSuppressed);
    AssertEqual(1L, snapshot.ApprovalRequestsSurfaced);
    AssertEqual(1L, snapshot.ApprovalResponsesHandled);
    AssertEqual(TimeSpan.FromMinutes(3), snapshot.AutonomousWorkObserved);
    return Task.CompletedTask;
}

static Task TestIntensityAsync()
{
    var previousContract = FocusContractSettings.Current;
    var previousIntensity = HapticSettings.MasterIntensity;
    try
    {
        FocusContractSettings.Current = FocusContract.For(FocusMode.Couch) with { IntensityMultiplier = 4f };
        HapticSettings.MasterIntensity = 2f;
        AssertEqual(1f, HapticSettings.EffectiveIntensity);

        FocusContractSettings.Current = FocusContract.For(FocusMode.SilentWatch) with { IntensityMultiplier = -2f };
        AssertEqual(0f, HapticSettings.EffectiveIntensity);
    }
    finally
    {
        FocusContractSettings.Current = previousContract;
        HapticSettings.MasterIntensity = previousIntensity;
    }
    return Task.CompletedTask;
}

static async Task TestLayeredSchedulerAsync()
{
    await using var controller = new RecordingController();
    await using var scheduler = new HapticScheduler(controller);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

    await scheduler.PlayAsync(HapticPatternCatalog.ApprovalRequired, timeout.Token);
    await controller.WaitForPlayCountAsync(1, timeout.Token);
    await scheduler.PlayAsync(HapticPatternCatalog.NavigationTick, timeout.Token);
    await controller.WaitForPlayCountAsync(3, timeout.Token);

    AssertEqual("approval-required", controller.Played[0]);
    AssertEqual("navigation-tick", controller.Played[1]);
    AssertEqual("approval-required", controller.Played[2]);
    await scheduler.StopAsync(timeout.Token);
}

static AgentEvent Event(AgentStateKind state, string? message = null) =>
    new("test", "session", state, DateTimeOffset.UtcNow, message);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

sealed class RecordingController : IControllerDevice
{
    private readonly object _sync = new();
    public List<string> Played { get; } = [];
    public string Id => "focus-test";
    public string DisplayName => "Focus test controller";
    public ControllerCapabilities Capabilities { get; } = new(false, true, true, true, true);
    public bool IsConnected => true;

    public async ValueTask PlayAsync(HapticPattern pattern, CancellationToken cancellationToken = default)
    {
        lock (_sync) Played.Add(pattern.Name);
        if (pattern.Loop)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return;
        }
        await Task.Delay(pattern.Duration, cancellationToken);
    }

    public ValueTask StopHapticsAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public async Task WaitForPlayCountAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_sync)
            {
                if (Played.Count >= count) return;
            }
            await Task.Delay(10, cancellationToken);
        }
    }

    public async IAsyncEnumerable<ControllerInputEvent> ReadEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
