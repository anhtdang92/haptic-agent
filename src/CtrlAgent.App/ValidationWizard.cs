using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using CtrlAgent.Core;

namespace CtrlAgent.App;

/// <summary>
/// Interactive hardware validation per docs/controller-validation.md. Walks
/// the operator through discovery/reconnect, standard controls, paddles,
/// simultaneous input, rumble, and an optional soak, then writes the evidence
/// report under validation/.
/// </summary>
internal sealed class ValidationWizard
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(45);

    private static readonly ControllerControl[] RequiredButtons =
    [
        ControllerControl.A,
        ControllerControl.B,
        ControllerControl.X,
        ControllerControl.Y,
        ControllerControl.Menu,
        ControllerControl.View,
        ControllerControl.Guide,
        ControllerControl.DPadUp,
        ControllerControl.DPadDown,
        ControllerControl.DPadLeft,
        ControllerControl.DPadRight,
        ControllerControl.LeftShoulder,
        ControllerControl.RightShoulder,
        ControllerControl.LeftThumbstickButton,
        ControllerControl.RightThumbstickButton,
    ];

    private static readonly ControllerControl[] RequiredAxes =
    [
        ControllerControl.LeftTrigger,
        ControllerControl.RightTrigger,
        ControllerControl.LeftThumbstickX,
        ControllerControl.LeftThumbstickY,
        ControllerControl.RightThumbstickX,
        ControllerControl.RightThumbstickY,
    ];

    private static readonly ControllerControl[] Paddles =
    [
        ControllerControl.PaddleLeft1,
        ControllerControl.PaddleLeft2,
        ControllerControl.PaddleRight1,
        ControllerControl.PaddleRight2,
    ];

    private readonly IControllerDevice _controller;
    private readonly CancellationToken _cancellation;
    private readonly Channel<ControllerInputEvent> _events = Channel.CreateUnbounded<ControllerInputEvent>();
    private readonly object _sync = new();
    private readonly HashSet<ControllerControl> _pressed = [];
    private readonly Dictionary<ControllerControl, float> _axes = [];
    private readonly List<ValidationCheck> _checks = [];
    private readonly StringBuilder _paddleObservations = new();
    private readonly StringBuilder _rumbleObservations = new();

    private ValidationWizard(IControllerDevice controller, CancellationToken cancellation)
    {
        _controller = controller;
        _cancellation = cancellation;
    }

    public static async Task<int> RunAsync(
        IControllerDevice controller,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var wizard = new ValidationWizard(controller, cancellationToken);
        return await wizard.RunCoreAsync(workingDirectory).ConfigureAwait(false);
    }

    private async Task<int> RunCoreAsync(string workingDirectory)
    {
        Console.WriteLine();
        Console.WriteLine("CtrlAgent hardware validation wizard");
        Console.WriteLine("Follow the prompts; every step can be skipped by typing 's'.");
        Console.WriteLine();

        var transport = await AskChoiceAsync(
            "Which transport is the controller using?",
            ["usb", "wireless-adapter", "bluetooth"]).ConfigureAwait(false);

        var pump = Task.Run(PumpEventsAsync, CancellationToken.None);

        await RunDiscoveryAndReconnectAsync().ConfigureAwait(false);
        await RunStandardControlsAsync().ConfigureAwait(false);
        await RunPaddlesAsync().ConfigureAwait(false);
        await RunSimultaneousInputAsync().ConfigureAwait(false);
        await RunRumbleAsync().ConfigureAwait(false);
        await RunSoakAsync().ConfigureAwait(false);

        Console.WriteLine();
        Console.Write("Any anomalies to record (empty for none)? ");
        var anomalies = await ReadLineAsync().ConfigureAwait(false);

        var report = new ValidationReport(
            _controller.DisplayName,
            _controller.Id,
            transport,
            Environment.OSVersion.VersionString,
            RuntimeInformation.FrameworkDescription,
            _controller.Capabilities,
            _checks,
            _paddleObservations.ToString().TrimEnd(),
            _rumbleObservations.ToString().TrimEnd(),
            anomalies,
            DateTimeOffset.Now);

        var directory = Path.Combine(workingDirectory, "validation");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ValidationReport.SuggestFileName(report.GeneratedAt, transport));
        await File.WriteAllTextAsync(path, report.ToMarkdown(), _cancellation).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Report written to {path}");
        Console.WriteLine(report.Recommendation);

        _events.Writer.TryComplete();
        _ = pump;
        return report.IsGo ? 0 : 1;
    }

    // ---- Step 1: discovery and reconnect -------------------------------

    private async Task RunDiscoveryAndReconnectAsync()
    {
        Console.WriteLine($"Connected device: {_controller.DisplayName} ({_controller.Id})");
        _checks.Add(new ValidationCheck(
            "discovery",
            "Discovery and identity",
            ValidationOutcome.Pass,
            $"{_controller.DisplayName} via {_controller.Id}"));

        Console.WriteLine();
        Console.WriteLine("Reconnect test: unplug or power off the controller now.");
        if (await SkipRequestedAsync().ConfigureAwait(false))
        {
            _checks.Add(new ValidationCheck(ValidationReport.ReconnectCheckId, "Disconnect and reconnect", ValidationOutcome.Skipped));
            return;
        }

        var disconnected = await WaitForEventAsync(
            inputEvent => inputEvent.Kind == ControllerInputEventKind.Disconnected,
            ReconnectTimeout).ConfigureAwait(false);

        if (!disconnected)
        {
            _checks.Add(new ValidationCheck(
                ValidationReport.ReconnectCheckId,
                "Disconnect and reconnect",
                ValidationOutcome.Fail,
                "No Disconnected event observed within the timeout."));
            return;
        }

        Console.WriteLine("Disconnect detected. Reconnect the controller now.");
        var reconnected = await WaitForEventAsync(
            inputEvent => inputEvent.Kind == ControllerInputEventKind.Connected,
            ReconnectTimeout).ConfigureAwait(false);

        _checks.Add(new ValidationCheck(
            ValidationReport.ReconnectCheckId,
            "Disconnect and reconnect",
            reconnected ? ValidationOutcome.Pass : ValidationOutcome.Fail,
            reconnected ? "Recovered without restarting the process." : "No Connected event observed within the timeout."));
        Console.WriteLine(reconnected ? "Reconnect detected." : "Reconnect NOT detected.");
    }

    // ---- Step 2: standard controls --------------------------------------

    private async Task RunStandardControlsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("Standard controls: press every button, pull both triggers fully,");
        Console.WriteLine("and move both sticks to their extremes. (Up to 120 seconds.)");
        if (await SkipRequestedAsync().ConfigureAwait(false))
        {
            _checks.Add(new ValidationCheck(ValidationReport.StandardControlsCheckId, "Standard controls", ValidationOutcome.Skipped));
            return;
        }

        var seen = new HashSet<ControllerControl>();
        var required = RequiredButtons.Concat(RequiredAxes).ToHashSet();

        await ConsumeEventsAsync(StepTimeout, inputEvent =>
        {
            var qualifies =
                (inputEvent.Kind == ControllerInputEventKind.Pressed && RequiredButtons.Contains(inputEvent.Control)) ||
                (inputEvent.Kind == ControllerInputEventKind.ValueChanged &&
                 RequiredAxes.Contains(inputEvent.Control) &&
                 Math.Abs(inputEvent.Value) >= 0.9f);

            if (qualifies && seen.Add(inputEvent.Control))
            {
                Console.WriteLine($"  ✓ {inputEvent.Control} ({seen.Count}/{required.Count})");
            }

            return seen.Count == required.Count;
        }).ConfigureAwait(false);

        var missing = required.Except(seen).ToArray();
        _checks.Add(new ValidationCheck(
            ValidationReport.StandardControlsCheckId,
            "Standard controls",
            missing.Length == 0 ? ValidationOutcome.Pass : ValidationOutcome.Fail,
            missing.Length == 0 ? "All controls observed." : $"Missing: {string.Join(", ", missing)}"));
    }

    // ---- Step 3: paddles -------------------------------------------------

    private async Task RunPaddlesAsync()
    {
        Console.WriteLine();

        if (!_controller.Capabilities.HasFourPaddles)
        {
            Console.WriteLine("Paddles: skipped — XInput fallback cannot expose Elite paddles.");
            _checks.Add(new ValidationCheck(
                ValidationReport.PaddlesCheckId,
                "Four independent paddles",
                ValidationOutcome.Skipped,
                "XInput fallback active; use the GameInput bridge for paddle validation."));
            return;
        }

        Console.WriteLine("Paddles: press each of the four paddles once. (Up to 120 seconds.)");
        if (await SkipRequestedAsync().ConfigureAwait(false))
        {
            _checks.Add(new ValidationCheck(ValidationReport.PaddlesCheckId, "Four independent paddles", ValidationOutcome.Skipped));
            return;
        }

        var seen = new HashSet<ControllerControl>();
        await ConsumeEventsAsync(StepTimeout, inputEvent =>
        {
            if (inputEvent.Kind == ControllerInputEventKind.Pressed &&
                Paddles.Contains(inputEvent.Control) &&
                seen.Add(inputEvent.Control))
            {
                Console.WriteLine($"  ✓ {inputEvent.Control} ({seen.Count}/4)");
            }

            return seen.Count == Paddles.Length;
        }).ConfigureAwait(false);

        var missing = Paddles.Except(seen).ToArray();
        var shadow = await AskYesNoAsync(
            "Did any paddle ALSO trigger a standard button (Xbox Accessories mapping)?").ConfigureAwait(false);

        _paddleObservations.AppendLine($"Observed paddles: {(seen.Count == 0 ? "none" : string.Join(", ", seen))}.");
        if (missing.Length > 0)
        {
            _paddleObservations.AppendLine($"Missing paddles: {string.Join(", ", missing)}.");
        }

        _paddleObservations.AppendLine(shadow
            ? "Paddles also emitted standard-button events; configure a neutral Xbox Accessories profile."
            : "Paddles emitted only their own paddle flags.");

        _checks.Add(new ValidationCheck(
            ValidationReport.PaddlesCheckId,
            "Four independent paddles",
            missing.Length == 0 && !shadow ? ValidationOutcome.Pass : ValidationOutcome.Fail,
            missing.Length == 0
                ? (shadow ? "All paddles seen, but they shadow standard buttons." : "All four paddles independent.")
                : $"Missing: {string.Join(", ", missing)}"));
    }

    // ---- Step 4: simultaneous input ---------------------------------------

    private async Task RunSimultaneousInputAsync()
    {
        Console.WriteLine();
        var usePaddles = _controller.Capabilities.HasFourPaddles;
        var combos = usePaddles
            ? new (string Name, Func<bool> Test)[]
            {
                ("paddle + face button", () => AnyPressed(Paddles) && AnyPressed(ControllerControl.A, ControllerControl.B, ControllerControl.X, ControllerControl.Y)),
                ("two paddles together", () => CountPressed(Paddles) >= 2),
                ("paddle + shoulder", () => AnyPressed(Paddles) && AnyPressed(ControllerControl.LeftShoulder, ControllerControl.RightShoulder)),
                ("trigger + button", () => AxisAbove(ControllerControl.RightTrigger, 0.5f) && AnyPressed(ControllerControl.A, ControllerControl.B, ControllerControl.X, ControllerControl.Y)),
                ("stick + paddle", () => (AxisAbove(ControllerControl.LeftThumbstickX, 0.5f) || AxisAbove(ControllerControl.LeftThumbstickY, 0.5f)) && AnyPressed(Paddles)),
            }
            : new (string Name, Func<bool> Test)[]
            {
                ("shoulder + face button", () => AnyPressed(ControllerControl.LeftShoulder, ControllerControl.RightShoulder) && AnyPressed(ControllerControl.A, ControllerControl.B, ControllerControl.X, ControllerControl.Y)),
                ("both shoulders together", () => CountPressed([ControllerControl.LeftShoulder, ControllerControl.RightShoulder]) == 2),
                ("trigger + button", () => AxisAbove(ControllerControl.RightTrigger, 0.5f) && AnyPressed(ControllerControl.A, ControllerControl.B, ControllerControl.X, ControllerControl.Y)),
                ("stick + shoulder", () => (AxisAbove(ControllerControl.LeftThumbstickX, 0.5f) || AxisAbove(ControllerControl.LeftThumbstickY, 0.5f)) && AnyPressed(ControllerControl.LeftShoulder, ControllerControl.RightShoulder)),
            };

        Console.WriteLine("Simultaneous input: perform each combination as it is announced.");
        if (await SkipRequestedAsync().ConfigureAwait(false))
        {
            _checks.Add(new ValidationCheck("simultaneous", "Simultaneous input", ValidationOutcome.Skipped));
            return;
        }

        var confirmed = new List<string>();
        foreach (var combo in combos)
        {
            Console.WriteLine($"  Hold: {combo.Name}");
            var observed = await ConsumeEventsAsync(TimeSpan.FromSeconds(30), _ => combo.Test()).ConfigureAwait(false);
            Console.WriteLine(observed ? "    ✓ detected" : "    ✗ not detected (timed out)");
            if (observed)
            {
                confirmed.Add(combo.Name);
            }
        }

        Console.WriteLine("Release all controls.");
        await Task.Delay(TimeSpan.FromSeconds(3), _cancellation).ConfigureAwait(false);
        DrainPendingEvents();
        bool stuck;
        lock (_sync)
        {
            stuck = _pressed.Count > 0;
        }

        _checks.Add(new ValidationCheck(
            "simultaneous",
            "Simultaneous input",
            confirmed.Count == combos.Length && !stuck ? ValidationOutcome.Pass : ValidationOutcome.Fail,
            $"{confirmed.Count}/{combos.Length} combinations detected{(stuck ? "; controls stuck after release" : "; no stuck controls")}."));
    }

    // ---- Step 5: rumble ----------------------------------------------------

    private async Task RunRumbleAsync()
    {
        Console.WriteLine();
        Console.WriteLine("Rumble: each cue plays once; answer whether you felt it.");
        if (await SkipRequestedAsync().ConfigureAwait(false))
        {
            _checks.Add(new ValidationCheck(ValidationReport.RumbleCheckId, "Distinct rumble cues", ValidationOutcome.Skipped));
            return;
        }

        var cues = new List<(string Name, HapticPattern Pattern, bool Supported)>
        {
            ("low thump", Single(0.8f, 0f, 0f, 0f, 250), _controller.Capabilities.HasLowFrequencyRumble),
            ("sharp tick", Single(0f, 0.8f, 0f, 0f, 80), _controller.Capabilities.HasHighFrequencyRumble),
            ("left trigger", Single(0f, 0f, 0.8f, 0f, 200), _controller.Capabilities.HasLeftTriggerRumble),
            ("right trigger", Single(0f, 0f, 0f, 0.8f, 200), _controller.Capabilities.HasRightTriggerRumble),
            ("completion", HapticPatternCatalog.Completed, true),
            ("error", HapticPatternCatalog.Error, true),
        };

        var felt = 0;
        var tested = 0;
        foreach (var (name, pattern, supported) in cues)
        {
            if (!supported)
            {
                _rumbleObservations.AppendLine($"{name}: not supported by this adapter.");
                continue;
            }

            tested++;
            Console.WriteLine($"  Playing '{name}'…");
            await _controller.PlayAsync(pattern, _cancellation).ConfigureAwait(false);
            var yes = await AskYesNoAsync($"  Did you feel '{name}'?").ConfigureAwait(false);
            _rumbleObservations.AppendLine($"{name}: {(yes ? "felt" : "NOT felt")}.");
            if (yes)
            {
                felt++;
            }
        }

        var intensity = Single(0.25f, 0f, 0f, 0f, 300).Frames
            .Concat(Single(0.5f, 0f, 0f, 0f, 300).Frames)
            .Concat(Single(0.75f, 0f, 0f, 0f, 300).Frames)
            .Concat(Single(1f, 0f, 0f, 0f, 300).Frames)
            .ToArray();
        Console.WriteLine("  Playing intensity sweep 0.25 → 1.00…");
        await _controller.PlayAsync(new HapticPattern("intensity-sweep", intensity), _cancellation).ConfigureAwait(false);
        var steps = await AskYesNoAsync("  Did the intensity clearly step up four times?").ConfigureAwait(false);
        _rumbleObservations.AppendLine($"intensity sweep 0.25–1.00: {(steps ? "distinct steps" : "steps NOT distinct")}.");

        var silent = await AskYesNoAsync("  Is the controller completely silent now?").ConfigureAwait(false);
        _rumbleObservations.AppendLine($"stop behavior: {(silent ? "rumble stopped cleanly" : "RUMBLE DID NOT STOP")}.");

        _checks.Add(new ValidationCheck(
            ValidationReport.RumbleCheckId,
            "Distinct rumble cues",
            felt >= 2 && silent ? ValidationOutcome.Pass : ValidationOutcome.Fail,
            $"{felt}/{tested} cues felt; stop {(silent ? "clean" : "FAILED")}."));
    }

    // ---- Step 6: soak -------------------------------------------------------

    private async Task RunSoakAsync()
    {
        Console.WriteLine();
        var run = await AskYesNoAsync(
            "Run a 60-second soak (repeated cues while you mash inputs)? The full 30-minute soak stays manual.").ConfigureAwait(false);

        if (!run)
        {
            _checks.Add(new ValidationCheck(
                "soak",
                "Sustained operation (60s)",
                ValidationOutcome.Skipped,
                "Run the full 30-minute soak from docs/controller-validation.md before release."));
            return;
        }

        var eventCount = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        var nextCue = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow < deadline && !_cancellation.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextCue)
            {
                await _controller.PlayAsync(HapticPatternCatalog.Completed, _cancellation).ConfigureAwait(false);
                nextCue = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            }

            eventCount += DrainPendingEvents();
            await Task.Delay(TimeSpan.FromMilliseconds(100), _cancellation).ConfigureAwait(false);
        }

        bool stuck;
        lock (_sync)
        {
            stuck = _pressed.Count > 0;
        }

        _checks.Add(new ValidationCheck(
            "soak",
            "Sustained operation (60s)",
            stuck ? ValidationOutcome.Fail : ValidationOutcome.Pass,
            $"{eventCount} input events processed; {(stuck ? "controls stuck at end" : "no stuck controls")}. Full 30-minute soak still recommended."));
    }

    // ---- Event plumbing -----------------------------------------------------

    private async Task PumpEventsAsync()
    {
        try
        {
            await foreach (var inputEvent in _controller.ReadEventsAsync(_cancellation).ConfigureAwait(false))
            {
                Track(inputEvent);
                _events.Writer.TryWrite(inputEvent);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[controller] stream ended: {exception.Message}");
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private void Track(ControllerInputEvent inputEvent)
    {
        lock (_sync)
        {
            switch (inputEvent.Kind)
            {
                case ControllerInputEventKind.Pressed:
                    _pressed.Add(inputEvent.Control);
                    break;
                case ControllerInputEventKind.Released:
                    _pressed.Remove(inputEvent.Control);
                    break;
                case ControllerInputEventKind.ValueChanged:
                    _axes[inputEvent.Control] = inputEvent.Value;
                    break;
                case ControllerInputEventKind.Disconnected:
                    _pressed.Clear();
                    _axes.Clear();
                    break;
            }
        }
    }

    /// <summary>Consumes events until the callback returns true or the timeout expires.</summary>
    private async Task<bool> ConsumeEventsAsync(TimeSpan timeout, Func<ControllerInputEvent, bool> until)
    {
        DrainPendingEvents();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        window.CancelAfter(timeout);

        try
        {
            while (await _events.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
            {
                while (_events.Reader.TryRead(out var inputEvent))
                {
                    if (until(inputEvent))
                    {
                        return true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!_cancellation.IsCancellationRequested)
        {
        }

        _cancellation.ThrowIfCancellationRequested();
        return false;
    }

    private Task<bool> WaitForEventAsync(Func<ControllerInputEvent, bool> predicate, TimeSpan timeout) =>
        ConsumeEventsAsync(timeout, predicate);

    private int DrainPendingEvents()
    {
        var count = 0;
        while (_events.Reader.TryRead(out _))
        {
            count++;
        }

        return count;
    }

    private bool AnyPressed(params ControllerControl[] controls)
    {
        lock (_sync)
        {
            return controls.Any(_pressed.Contains);
        }
    }

    private int CountPressed(IReadOnlyList<ControllerControl> controls)
    {
        lock (_sync)
        {
            return controls.Count(_pressed.Contains);
        }
    }

    private bool AxisAbove(ControllerControl control, float threshold)
    {
        lock (_sync)
        {
            return _axes.TryGetValue(control, out var value) && Math.Abs(value) >= threshold;
        }
    }

    private static HapticPattern Single(float low, float high, float left, float right, int milliseconds) =>
        new(
            "validation",
            [RumbleFrame.Create(low, high, left, right, TimeSpan.FromMilliseconds(milliseconds))]);

    // ---- Console helpers ------------------------------------------------------

    private async Task<string> ReadLineAsync() =>
        (await Console.In.ReadLineAsync(_cancellation).ConfigureAwait(false))?.Trim() ?? string.Empty;

    private async Task<bool> SkipRequestedAsync()
    {
        Console.Write("Press Enter to start (or 's' to skip): ");
        var line = await ReadLineAsync().ConfigureAwait(false);
        return line.Equals("s", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> AskYesNoAsync(string question)
    {
        while (true)
        {
            Console.Write($"{question} [y/n] ");
            var line = await ReadLineAsync().ConfigureAwait(false);
            if (line.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (line.Equals("n", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
    }

    private async Task<string> AskChoiceAsync(string question, string[] choices)
    {
        while (true)
        {
            Console.WriteLine(question);
            for (var index = 0; index < choices.Length; index++)
            {
                Console.WriteLine($"  {index + 1}. {choices[index]}");
            }

            Console.Write("> ");
            var line = await ReadLineAsync().ConfigureAwait(false);
            if (int.TryParse(line, out var selection) && selection >= 1 && selection <= choices.Length)
            {
                return choices[selection - 1];
            }
        }
    }
}
