using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CtrlAgent.Adapters.Mock;
using CtrlAgent.Core;
using CtrlAgent.Hosting;

namespace CtrlAgent.Gui;

/// <summary>
/// Stand-in for the Windows-only dictation service. Scripted so the voice
/// overlay can be rendered in its realistic states.
/// </summary>
public sealed class SpeechToTextService : IDisposable
{
    /// <summary>When set, recognition "hears" this instead of failing.</summary>
    public static string? ScriptedResult { get; set; }

    public event Action<string>? HypothesisChanged;

    public string? UnavailableReason { get; private set; } =
        ScriptedResult is null ? "no microphone in the render harness" : null;

    public bool EnsureInitialized() => ScriptedResult is not null;

    public Task<string?> RecognizeOnceAsync()
    {
        if (ScriptedResult is { } text)
        {
            HypothesisChanged?.Invoke(text);
        }

        return Task.FromResult(ScriptedResult);
    }

    public void CancelRecognition() { }

    public void Dispose() { }
}

/// <summary>The real App.axaml styles with a headless-safe code-behind.</summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // The window code-behind calls these; the harness renders windows directly.
    public void ShowMainframe() { }

    public void ToggleOverlay() { }
}

internal sealed class IdleController : IControllerDevice
{
    private readonly Channel<ControllerInputEvent> _events = Channel.CreateUnbounded<ControllerInputEvent>();

    public string Id => "harness";

    public string DisplayName => "Xbox Elite Series 2 (harness)";

    public ControllerCapabilities Capabilities { get; } = new(true, true, true, true, true);

    public bool IsConnected => true;

    public async IAsyncEnumerable<ControllerInputEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var e in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return e;
        }
    }

    public ValueTask PlayAsync(HapticPattern pattern, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask StopHapticsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class SingleProvider(IControllerDevice device) : IControllerProvider
{
    public ValueTask<IControllerDevice?> GetPrimaryControllerAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IControllerDevice?>(device);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class Harness
{
    private static string OutDir =>
        Environment.GetEnvironmentVariable("UIRENDER_OUT") ?? "shots";

    public static int Main()
    {
        Directory.CreateDirectory(OutDir);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        var options = new GuiOptions(
            "mock",
            Environment.CurrentDirectory,
            "Inspect the repository and continue the highest-priority task.",
            null, null, null, null);

        var engine = new HostEngine(
            new SingleProvider(new IdleController()),
            new MockAgentAdapter(),
            ControllerProfile.Default,
            new HostEngineOptions(options.DefaultPrompt));

        var viewModel = new MainViewModel(engine, options);
        viewModel.AttachEngine(engine);

        // Populate the surfaces so the render shows real content, not empties.
        viewModel.ControllerStatus = "Xbox Elite Series 2 (paddles)";
        viewModel.AgentStatus = "claude";
        viewModel.AgentState = "Working";
        viewModel.IsAgentActive = true;
        viewModel.SessionId = "sess-8f21c0a4";
        viewModel.PromptText = "Refactor the mapping engine tests";
        viewModel.AppendLog("Agent adapter 'claude' started.");
        viewModel.AppendLog("Controller connected: Xbox Elite Series 2");
        viewModel.AppendLog("[agent] Working: Bash: dotnet test");
        viewModel.AppendLog("[agent] Error: build failed in CtrlAgent.Core");
        viewModel.AppendLog("[agent] Completed: All tests pass (12.4s · 3 turns · $0.09)");

        Render(new MainWindow { DataContext = viewModel }, "01-main-window.png");

        // The window at its declared MinWidth. Worth its own shot: the status
        // card once packed the facts and the session knobs into one row, which
        // fit the developer's window and drew the knobs straight over WORKSPACE
        // and PROFILE at the default size — a class of bug that is invisible
        // until something renders narrow.
        Render(new MainWindow { DataContext = viewModel }, "16-main-narrow.png", 740, 660);

        // Approval state: banner + highlighted controls.
        var approving = new MainViewModel(engine, options);
        approving.AttachEngine(engine);
        approving.ControllerStatus = "Xbox Elite Series 2 (paddles)";
        approving.AgentStatus = "claude";
        approving.AgentState = "ApprovalRequired";
        approving.HasPendingApproval = true;
        approving.PendingApprovalMessage = "Claude Code wants: Write: src/CtrlAgent.Core/Mapping.cs";
        approving.AppendLog("[agent] ApprovalRequired: Claude Code wants: Write: src/Mapping.cs");
        Render(new MainWindow { DataContext = approving }, "02-main-approval.png");

        // The boot sequence, sampled across its timeline. The animation clock
        // DOES advance here — Pump sleeps in real time between render ticks —
        // so these are genuine frames of the running animation, not mock-ups.
        // Without them the intro is the one part of the app nobody can review:
        // it plays for three seconds on a machine none of us is sitting at.
        foreach (var (at, name) in new[]
                 {
                     (250, "20-boot-strike.png"),
                     (700, "21-boot-field.png"),
                     (1250, "22-boot-badge.png"),
                     (1900, "23-boot-online.png"),
                     (3400, "24-boot-settled.png"),
                 })
        {
            RenderAt(new MainframeWindow { DataContext = new MainframeViewModel(viewModel) },
                name, 1600, 900, at);
        }

        var big = new MainframeViewModel(viewModel);
        Render(new MainframeWindow { DataContext = big }, "03-mainframe.png", 1600, 900,
            afterShow: w =>
            {
                // Retired immediately so this shot shows the settled UI rather
                // than whatever frame of the intro the pump happens to land on.
                if (w.FindControl<Border>("IntroOverlay") is { } intro)
                {
                    intro.IsVisible = false;
                }
            });

        var bigApproval = new MainframeViewModel(approving);
        Render(new MainframeWindow { DataContext = bigApproval }, "04-mainframe-approval.png", 1600, 900,
            afterShow: w =>
            {
                if (w.FindControl<Border>("IntroOverlay") is { } intro)
                {
                    intro.IsVisible = false;
                }
            });

        // A real conversation: drive the mock agent end to end so the
        // transcript fills with genuine bubbles and activity rows.
        var chat = new MainViewModel(engine, options);
        chat.AttachEngine(engine);
        chat.ControllerStatus = "Xbox Elite Series 2 (paddles)";
        chat.AgentStatus = "claude";
        engine.StartAsync().GetAwaiter().GetResult();
        chat.SubmitPromptText("Add a session picker to Mainframe mode");
        Pump(3000);
        chat.SubmitPromptText("Now write tests for it");
        Pump(3000);
        Render(new MainWindow { DataContext = chat }, "06-conversation.png");

        // First-run setup overlay.
        var setup = new MainViewModel(null, options) { IsSetupVisible = true };
        Render(new MainWindow { DataContext = setup }, "07-first-run.png");

        // Startup failure. A dead host used to be indistinguishable from a
        // disconnected one, so this surface is worth a render check.
        var failed = new MainViewModel(null, options)
        {
            StartupError = "Unknown argument: Files\\Archived\\Coding_Projects\\haptic-agent",
            ControllerStatus = "Failed to start",
            AgentStatus = "Failed to start",
        };
        Render(new MainWindow { DataContext = failed }, "13-startup-error.png");

        // Profile editor.
        Render(new ProfileEditorWindow(engine), "08-profile-editor.png", 940, 660);

        // Overlay HUD, with an approval pending. Built fresh: the shared
        // engine's completed turns clear the approval on older view models.
        var hud = new MainViewModel(engine, options);
        hud.AttachEngine(engine);
        hud.AgentState = "ApprovalRequired";
        hud.HasPendingApproval = true;
        hud.PendingApprovalMessage = "Claude Code wants: Write: src/CtrlAgent.Core/Mapping.cs";
        Render(new OverlayWindow { DataContext = hud }, "09-overlay.png", 380, 150);

        // Notification toast.
        var toast = new ToastWindow();
        toast.Configure(
            "APPROVAL REQUIRED",
            "Claude Code wants: Write: src/CtrlAgent.Core/Mapping.cs",
            "#FFB020",
            showApprovalButtons: true);
        Render(toast, "10-toast.png", 340, 150);

        // Mainframe: the fullscreen shortcuts screen.
        var shortcuts = new MainframeViewModel(chat);
        Render(new MainframeWindow { DataContext = shortcuts }, "11-shortcuts.png", 1600, 900,
            afterShow: HideIntro,
            afterSettle: _ => shortcuts.OnKey("F1"));

        // Mainframe: the voice overlay, mid-review of a transcript.
        SpeechToTextService.ScriptedResult = "Refactor the mapping engine and run the tests";
        var voice = new MainframeViewModel(chat);
        Render(new MainframeWindow { DataContext = voice }, "12-voice.png", 1600, 900,
            afterShow: HideIntro,
            afterSettle: _ => voice.OnKey("F2"));
        SpeechToTextService.ScriptedResult = null;

        // Settled: Interrupt drops out of the HUD once there is nothing to
        // interrupt. 03 covers the mid-turn set, 04 the approval set.
        var settled = new MainViewModel(null, options) { AgentStatus = "claude" };
        settled.AttachEngine(engine);
        settled.AgentState = "Completed";
        settled.IsAgentActive = false;
        var idle = new MainframeViewModel(settled);
        Render(new MainframeWindow { DataContext = idle }, "14-mainframe-idle.png", 1600, 900,
            afterShow: HideIntro);

        // The settings panel — the only place focus navigation exists now.
        var settings = new MainframeViewModel(viewModel);
        Render(new MainframeWindow { DataContext = settings }, "05-mainframe-settings.png", 1600, 900,
            afterShow: HideIntro,
            afterSettle: _ =>
            {
                settings.ToggleSettings();
                settings.OnKey("Right");
            });

        // The workspace picker, with a couple of remembered directories.
        Render(
            new WorkspaceWindow(
                "/home/user/haptic-agent",
                ["/home/user/other-project", "/home/user/scratch/spike"]),
            "15-workspace.png", 620, 440);

        if (Faults.Count > 0)
        {
            Console.Error.WriteLine($"\n{Faults.Count} layout fault(s):");
            foreach (var fault in Faults)
            {
                Console.Error.WriteLine($"  {fault}");
            }

            return 1;
        }

        Console.WriteLine("done — no layout faults");
        return 0;
    }

    /// <summary>The boot intro is opaque until its animation finishes; the
    /// harness clock does not advance, so retire it manually.</summary>
    private static void HideIntro(Window window)
    {
        if (window.FindControl<Border>("IntroOverlay") is { } intro)
        {
            intro.IsVisible = false;
        }
    }

    /// <summary>
    /// Runs the dispatcher for roughly the given wall-clock time while
    /// driving the headless render timer, so bindings settle, animations
    /// advance, and a fresh frame is actually produced.
    /// </summary>
    /// <summary>
    /// Drives layout, bindings and the render timer for a span of <em>wall
    /// clock</em> time. Avalonia's headless animation clock follows real time,
    /// so this genuinely advances animations — the harness once claimed it did
    /// not, and every intro shot was taken on that false assumption.
    /// <para>
    /// Timed against a Stopwatch rather than by counting sleeps: each iteration
    /// also does render work, so counting <c>n × 25ms</c> overshot badly and
    /// the "t=250ms" frames were really landing past a second.
    /// </para>
    /// </summary>
    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var remaining = milliseconds - (int)clock.ElapsedMilliseconds;
            if (remaining > 0)
            {
                Thread.Sleep(Math.Min(8, remaining));
            }
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    /// <summary>Reports whether animation-gated elements are actually visible.</summary>
    private static void Diagnose(Visual root, string label)
    {
        foreach (var visual in root.GetSelfAndVisualDescendants())
        {
            if (visual is not Border border)
            {
                continue;
            }

            var classes = string.Join(" ", border.Classes);
            if (!classes.Contains("approvalBanner") && !classes.Contains("introFade"))
            {
                continue;
            }

            Console.WriteLine(
                $"  [{label}] Border[{classes}] IsVisible={border.IsVisible} " +
                $"Opacity={border.Opacity:0.###} Bounds={border.Bounds}");
        }
    }

    /// <summary>
    /// Layout faults found across every rendered surface. Collected rather
    /// than thrown so one bad window does not hide the rest, and reported as a
    /// nonzero exit so CI fails instead of quietly uploading a broken picture.
    /// </summary>
    private static readonly List<string> Faults = [];

    /// <summary>
    /// Checks that every card actually got drawn somewhere useful. This is the
    /// cheap version of looking at the screenshots, and it catches the failure
    /// mode a type checker never will: a card squeezed to nothing by a
    /// neighbour, or one laid out past the window edge. Both have happened —
    /// ACTIVE BINDINGS once vanished entirely on a short window because the
    /// mirror card above it was Top-docked and took its full desired height.
    /// </summary>
    private static void CheckCards(Window window, string label)
    {
        var frame = new Rect(window.Bounds.Size);
        foreach (var visual in window.GetSelfAndVisualDescendants())
        {
            if (visual is not Border { IsVisible: true } border ||
                !border.Classes.Contains("card") ||
                border.GetTransformedBounds() is not { } transformed)
            {
                continue;
            }

            var bounds = transformed.Bounds.TransformToAABB(transformed.Transform);
            if (bounds.Width < 8 || bounds.Height < 8)
            {
                Faults.Add($"{label}: a .card collapsed to {bounds.Width:0}x{bounds.Height:0}.");
            }
            else if (!frame.Intersects(bounds))
            {
                Faults.Add($"{label}: a .card sits outside the window at {bounds}.");
            }
            else if (EffectiveOpacity(border) < 0.05)
            {
                // Correct bounds, invisible anyway. An entry animation whose
                // delay outlives the capture leaves every card laid out and
                // fully transparent, which every geometric check happily
                // passes while the screenshot shows only the wallpaper.
                Faults.Add($"{label}: a .card is laid out but fully transparent.");
            }
        }
    }

    /// <summary>
    /// Captures a surface at a chosen moment on its animation timeline.
    /// Deliberately skips the card checks: a card caught mid-animation is
    /// legitimately scaled or transparent, and asserting on it would fail the
    /// build for a working intro.
    /// </summary>
    private static void RenderAt(Window window, string fileName, int width, int height, int atMilliseconds)
    {
        window.Width = width;
        window.Height = height;
        window.Show();
        Pump(atMilliseconds);

        var frame = window.GetLastRenderedFrame();
        if (frame is null)
        {
            Faults.Add($"{fileName}: no frame was rendered.");
        }
        else
        {
            var path = Path.Combine(OutDir, fileName);
            frame.Save(path);
            Console.WriteLine($"wrote {path} (t={atMilliseconds}ms)");
        }

        window.Close();
    }

    /// <summary>Opacity as actually seen: this element's, times every ancestor's.</summary>
    private static double EffectiveOpacity(Visual visual)
    {
        var opacity = 1d;
        for (Visual? node = visual; node is not null; node = node.GetVisualParent())
        {
            opacity *= node.Opacity;
        }

        return opacity;
    }

    private static void Render(
        Window window,
        string fileName,
        int? width = null,
        int? height = null,
        Action<Window>? afterShow = null,
        Action<Window>? afterSettle = null)
    {
        if (width is { } w && height is { } h)
        {
            window.Width = w;
            window.Height = h;
        }

        window.Show();
        afterShow?.Invoke(window);

        // Let layout, bindings, animations, and a render frame settle.
        Pump(1200);

        if (afterSettle is not null)
        {
            afterSettle(window);
            Pump(900);
        }

        Diagnose(window, fileName);
        CheckCards(window, fileName);

        var frame = window.GetLastRenderedFrame();
        if (frame is null)
        {
            // Was a bare Console.WriteLine returning 0, so a surface that
            // never rendered still passed CI.
            Faults.Add($"{fileName}: no frame was rendered.");
            window.Close();
            return;
        }

        var path = Path.Combine(OutDir, fileName);
        frame.Save(path);
        Console.WriteLine($"wrote {path} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
        window.Close();
    }
}
