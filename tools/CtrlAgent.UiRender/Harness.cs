using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CtrlAgent.Adapters.Mock;
using CtrlAgent.Core;
using CtrlAgent.Hosting;
using CtrlAgent.Presentation;

namespace CtrlAgent.Gui;

/// <summary>
/// Stand-in for the Windows-only dictation service. Scripted so the voice
/// overlay can be rendered in its realistic states.
/// </summary>
public sealed class SpeechToTextService : IDisposable
{
    /// <summary>When set, recognition "hears" this instead of failing.</summary>
    public static string? ScriptedResult { get; set; }

    /// <summary>Mirrors the real service's device selection surface.</summary>
    public static string? PreferredMicrophone { get; set; }

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

        // The recents sidebar, populated the way a Claude workspace would be.
        var noop = new RelayCommand(_ => { });
        viewModel.ShowSessionList(
        [
            new SessionListItem("8f21c0a4", "Refactor the mapping engine tests", DateTimeOffset.UtcNow.AddMinutes(-3), isCurrent: true, noop),
            new SessionListItem("2b77d1ee", "Add a session picker to Mainframe mode and wire it to the d-pad", DateTimeOffset.UtcNow.AddHours(-5), isCurrent: false, noop),
            new SessionListItem("77aa19c2", "Fix the DualSense trigger effect", DateTimeOffset.UtcNow.AddDays(-2), isCurrent: false, noop),
        ]);

        RenderButtonGallery();
        CheckPressDarkens();

        Render(new MainWindow { DataContext = viewModel }, "01-main-window.png");

        // The window at its declared MinWidth. Worth its own shot: the status
        // card once packed the facts and the session knobs into one row, which
        // fit the developer's window and drew the knobs straight over WORKSPACE
        // and PROFILE at the default size — a class of bug that is invisible
        // until something renders narrow.
        Render(new MainWindow { DataContext = viewModel }, "16-main-narrow.png", 740, 660);

        // The desktop all-shortcuts overlay (F1): every binding, full height.
        viewModel.IsShortcutsVisible = true;
        Render(new MainWindow { DataContext = viewModel }, "29-shortcuts-overlay.png");
        viewModel.IsShortcutsVisible = false;

        // Approval state: banner + highlighted controls.
        var approving = new MainViewModel(engine, options);
        approving.AttachEngine(engine);
        approving.ControllerStatus = "Xbox Elite Series 2 (paddles)";
        approving.AgentStatus = "claude";
        approving.AgentState = "ApprovalRequired";
        approving.HasPendingApproval = true;
        // Drive the bot with the real event so the bubble coaches the
        // approval, as it does live — a hand-set flag skips OnAgentEvent and
        // left the shot showing stale "send me a prompt" advice.
        approving.Buddy.OnAgentEvent(new AgentEvent(
            "claude", "sess", AgentStateKind.ApprovalRequired, DateTimeOffset.UtcNow,
            "Claude Code wants: Write: src/CtrlAgent.Core/Mapping.cs", "req-1"));
        approving.PendingApprovalMessage = "Claude Code wants: Write: src/CtrlAgent.Core/Mapping.cs";
        approving.AppendLog("[agent] ApprovalRequired: Claude Code wants: Write: src/Mapping.cs");
        Render(new MainWindow { DataContext = approving }, "02-main-approval.png");

        // The diff review panel: what you are about to approve, before you
        // approve more of it. Populated through PresentDiff so the shot needs
        // no git repository — the collection path is unit-tested separately.
        var sampleChanges = new WorkspaceChanges(
        [
            new DiffFile("src/CtrlAgent.Core/Mapping.cs", DiffFileChange.Modified, false, 3, 1,
            [
                new DiffLine(DiffLineKind.HunkHeader, "@@ -118,6 +118,8 @@ public IReadOnlyList<AgentCommand> Process(ControllerInputEvent input)"),
                new DiffLine(DiffLineKind.Context, "         var matches = FindStructuralMatches(input);"),
                new DiffLine(DiffLineKind.Removed, "-        return matches;"),
                new DiffLine(DiffLineKind.Added, "+        var eligible = FilterByEligibility(matches);"),
                new DiffLine(DiffLineKind.Added, "+        return Hydrate(eligible);"),
                new DiffLine(DiffLineKind.HunkHeader, "@@ -204,3 +206,4 @@ private void Reset()"),
                new DiffLine(DiffLineKind.Context, "         _pendingRequestId = null;"),
                new DiffLine(DiffLineKind.Added, "+        _pendingSessionId = null;"),
            ]),
            new DiffFile("tests/CtrlAgent.Tests/Program.cs", DiffFileChange.Modified, false, 1, 1,
            [
                new DiffLine(DiffLineKind.HunkHeader, "@@ -12,7 +12,7 @@"),
                new DiffLine(DiffLineKind.Removed, "-    (\"Plain A submits\", TestPlainMappingAsync),"),
                new DiffLine(DiffLineKind.Added, "+    (\"Plain A submits a prompt\", TestPlainMappingAsync),"),
            ]),
            new DiffFile("src/CtrlAgent.Gui/DiffRow.cs", DiffFileChange.Added, false, 2, 0,
            [
                new DiffLine(DiffLineKind.Added, "+using Avalonia.Media;"),
                new DiffLine(DiffLineKind.Added, "+using CtrlAgent.Presentation;"),
            ]),
            new DiffFile("assets/logo.png", DiffFileChange.Modified, true, 0, 0, []),
        ]);

        var reviewing = new MainViewModel(engine, options);
        reviewing.AttachEngine(engine);
        reviewing.ControllerStatus = "Xbox Elite Series 2 (paddles)";
        reviewing.AgentStatus = "claude";
        reviewing.IsDiffVisible = true;
        reviewing.PresentDiff(sampleChanges);
        Render(new MainWindow { DataContext = reviewing }, "18-diff-review.png",
            afterSettle: w =>
            {
                if (w.FindControl<ScrollViewer>("DiffScroller") is not { } scroller ||
                    scroller.Bounds.Height < 40 ||
                    scroller.Extent.Height < 40)
                {
                    Faults.Add("18-diff-review: the diff rows did not lay out.");
                }

                // The file rail: one link per file, and jumping marks the
                // target current. (The scroll itself can't move here — the
                // sample diff is shorter than the viewport — so the anchor
                // arithmetic is proven in the unit tests instead.)
                var links = w.GetVisualDescendants()
                    .OfType<Button>()
                    .Count(button => button.Classes.Contains("filelink") && button.IsEffectivelyVisible);
                if (links != 4)
                {
                    Faults.Add($"18-diff-review: expected 4 file-rail links, found {links}.");
                }

                reviewing.JumpToDiffFile(3);
                if (!reviewing.DiffFiles[3].IsCurrent || reviewing.DiffFiles[0].IsCurrent)
                {
                    Faults.Add("18-diff-review: jumping to a file did not mark it current.");
                }

                reviewing.JumpToDiffFile(0);
            });

        // The same panel over a clean tree: the empty state has to explain
        // itself, not look like a failed load.
        var cleanTree = new MainViewModel(engine, options);
        cleanTree.AttachEngine(engine);
        cleanTree.AgentStatus = "claude";
        cleanTree.IsDiffVisible = true;
        cleanTree.PresentDiff(new WorkspaceChanges([]));
        Render(new MainWindow { DataContext = cleanTree }, "19-diff-clean.png");

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

        // Sessions before the Mainframe view model exists: the Sessions tile
        // is added at construction only when the adapter's store is readable.
        chat.ShowSessionList(
        [
            new SessionListItem("8f21c0a4", "Refactor the mapping engine tests", DateTimeOffset.UtcNow.AddMinutes(-3), isCurrent: true, noop),
            new SessionListItem("2b77d1ee", "Add a session picker to Mainframe mode and wire it to the d-pad", DateTimeOffset.UtcNow.AddHours(-5), isCurrent: false, noop),
            new SessionListItem("77aa19c2", "Fix the DualSense trigger effect", DateTimeOffset.UtcNow.AddDays(-2), isCurrent: false, noop),
        ]);

        // Subscribed before the turns run, so the Mainframe feed captures the
        // same conversation — the populated-feed shot below depends on it.
        var mainframeChat = new MainframeViewModel(chat);

        engine.StartAsync().GetAwaiter().GetResult();
        chat.SubmitPromptText("Add a session picker to Mainframe mode");
        Pump(3000);
        chat.SubmitPromptText("Now write tests for it");
        Pump(3000);

        // The Mainframe conversation: user bubbles right, agent markdown
        // left, composer under it. This is the surface the user actually
        // chats in from the couch, so it gets its own populated shot.
        Render(new MainframeWindow { DataContext = mainframeChat }, "26-mainframe-chat.png", 1600, 900,
            afterShow: HideIntro);

        // The same diff panel, projected into Mainframe: readable from the
        // couch before approving more of it. State is reset afterwards —
        // later shots reuse this view model.
        chat.IsDiffVisible = true;
        chat.PresentDiff(sampleChanges);
        Render(new MainframeWindow { DataContext = mainframeChat }, "27-mainframe-diff.png", 1600, 900,
            afterShow: HideIntro,
            afterSettle: w =>
            {
                if (w.FindControl<ScrollViewer>("MainframeDiffScroller") is not { } scroller ||
                    scroller.Extent.Height < 40)
                {
                    Faults.Add("27-mainframe-diff: the diff rows did not lay out.");
                }
            });
        chat.IsDiffVisible = false;

        // The couch session picker, with the middle row focused.
        Render(new MainframeWindow { DataContext = mainframeChat }, "28-mainframe-sessions.png", 1600, 900,
            afterShow: HideIntro,
            afterSettle: _ =>
            {
                mainframeChat.ShowSessions();
                mainframeChat.OnKey("Down");
            });
        mainframeChat.OnKey("Escape");

        // A markdown-rich agent reply, injected directly: the mock agent
        // speaks plain prose, and this shot is what proves bold, inline code,
        // bullets, and a fenced block actually render as styled blocks.
        chat.Transcript.Add(new ChatMessage
        {
            IsUser = false,
            IsActivity = false,
            Text = "Here is the plan:\n\n## Mapping fix\n- Prefer **chords** over plain buttons in `MappingEngine.Process`\n- Keep *eligibility* filtering last\n\n```csharp\nvar commands = engine.Process(input);\n```",
        });
        Render(new MainWindow { DataContext = chat }, "06-conversation.png");

        // First-run setup overlay.
        var setup = new MainViewModel(null, options) { IsSetupVisible = true };
        Render(new MainWindow { DataContext = setup }, "07-first-run.png");

        // Setup after a failed preflight: the screen a fresh machine actually
        // sees when the CLI is not installed or a saved workspace was deleted.
        // The message must read as instructions, not as a stack trace.
        var blocked = new MainViewModel(null, options)
        {
            IsSetupVisible = true,
            SetupAgent = "claude",
            SetupError =
                "The claude CLI was not found on PATH. Install it with: npm install -g @anthropic-ai/claude-code — " +
                "then run 'claude' once in a terminal to sign in before using CtrlAgent.\n" +
                "The workspace folder does not exist: C:\\repos\\old-project. " +
                "Pick another folder — it may have been moved or deleted since last time.",
        };
        Render(new MainWindow { DataContext = blocked }, "25-setup-preflight.png");

        // Guide-button confirmation before Mainframe takes the screen.
        var mainframePrompt = new MainViewModel(null, options) { IsMainframePromptVisible = true };
        Render(
            new MainWindow { DataContext = mainframePrompt },
            "15-mainframe-prompt.png",
            expectVisibleText: "ENTER MAINFRAME?");

        // Startup failure. A dead host used to be indistinguishable from a
        // disconnected one, so this surface is worth a render check.
        var failed = new MainViewModel(null, options)
        {
            StartupError = "Unknown argument: Files\\Archived\\Coding_Projects\\haptic-agent",
            ControllerStatus = "Failed to start",
            AgentStatus = "Failed to start",
        };
        Render(
            new MainWindow { DataContext = failed },
            "13-startup-error.png",
            expectVisibleText: "COULDN'T START");

        // Profile editor.
        Render(new ProfileEditorWindow(engine), "08-profile-editor.png", 940, 660);

        // Overlay HUD, with an approval pending. Built fresh: the shared
        // engine's completed turns clear the approval on older view models.
        var hud = new MainViewModel(engine, options);
        hud.AttachEngine(engine);
        hud.AgentState = "ApprovalRequired";
        hud.HasPendingApproval = true;
        hud.PendingApprovalMessage = "Claude Code wants: Write: src/CtrlAgent.Core/Mapping.cs";
        hud.Buddy.OnAgentEvent(new AgentEvent(
            "claude", "sess", AgentStateKind.ApprovalRequired, DateTimeOffset.UtcNow,
            "Claude Code wants: Write: src/CtrlAgent.Core/Mapping.cs", "req-1"));
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

        // Sony flavor: the same hub with a DualSense attached — face chips
        // render Sony's shapes in Sony's colors and the mirror flips its
        // legend. The flag is app-global (set by ControllerConnected live), so
        // mint this view model's rows while it is up and restore the Xbox
        // flavor before any later shot.
        ChordToken.PlayStation = true;
        var sony = new MainViewModel(null, options) { AgentStatus = "claude" };
        sony.AttachEngine(engine);
        sony.ControllerStatus = "DualSense Edge";
        sony.ControllerVisual.SetPlayStationFlavor(true);
        Render(new MainframeWindow { DataContext = new MainframeViewModel(sony) },
            "30-mainframe-ps.png", 1600, 900,
            afterShow: HideIntro);
        ChordToken.PlayStation = false;

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

    /// <summary>
    /// Every button variant in every state, side by side.
    /// <para>
    /// Buttons are the one part of the design system that cannot be judged from
    /// the app screenshots: nothing in them is hovered, focused, pressed or
    /// disabled, so most of what a button style says is invisible. Pseudo-classes
    /// are forced here so all of it renders at once.
    /// </para>
    /// </summary>
    private static void RenderButtonGallery()
    {
        string[] variants = ["accent", "", "approve", "deny", "ghost", "tool", "knob"];
        string[] states = ["rest", ":pointerover", "disabled"];

        var grid = new StackPanel { Spacing = 22, Margin = new Thickness(28) };
        grid.Children.Add(new TextBlock
        {
            Text = "BUTTON MATRIX — rest / hover / disabled (press is measured, see CheckPressDarkens)",
            FontSize = 12,
            Foreground = Brushes.SlateGray,
        });

        foreach (var variant in variants)
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(variant) ? "(default)" : variant,
                Width = 90,
                FontSize = 12,
                Foreground = Brushes.Gray,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            foreach (var state in states)
            {
                var button = new Button { Content = state == "rest" ? "Submit" : state.TrimStart(':') };
                if (!string.IsNullOrEmpty(variant))
                {
                    button.Classes.Add(variant);
                }

                if (state == "disabled")
                {
                    button.IsEnabled = false;
                }
                else if (state != "rest")
                {
                    ((IPseudoClasses)button.Classes).Set(state, true);
                }

                row.Children.Add(button);
            }

            grid.Children.Add(row);
        }

        var window = new Window
        {
            Width = 760,
            Height = 460,
            Content = new ScrollViewer { Content = grid },
        };

        Render(window, "17-buttons.png");
    }

    /// <summary>
    /// Presses each button variant with real input and requires the surface to
    /// get <em>darker</em>.
    /// <para>
    /// This is an assertion rather than a screenshot because <c>:pressed</c> is
    /// driven by the read-only <c>Button.IsPressed</c> and cannot be forced, and
    /// because real input can only press one button at a time — a matrix column
    /// of pressed buttons is not renderable.
    /// </para>
    /// <para>
    /// It exists because <c>approve</c> and <c>deny</c> had drifted to
    /// rest #26 → hover #4D → pressed #66: the harder you pushed, the brighter
    /// they got. Nothing catches that except looking, and nobody was.
    /// </para>
    /// </summary>
    private static void CheckPressDarkens()
    {
        foreach (var variant in new[] { "accent", "", "approve", "deny", "ghost", "tool", "knob" })
        {
            var name = string.IsNullOrEmpty(variant) ? "(default)" : variant;
            var button = new Button { Content = "Press me", Margin = new Thickness(40) };
            if (!string.IsNullOrEmpty(variant))
            {
                button.Classes.Add(variant);
            }

            var window = new Window { Width = 320, Height = 140, Content = button };
            window.Show();
            Pump(320);

            var presenter = button.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .FirstOrDefault(candidate => candidate.Name == "PART_ContentPresenter");
            if (presenter is null || button.GetTransformedBounds() is not { } placed)
            {
                Faults.Add($"buttons: could not measure the '{name}' variant.");
                window.Close();
                continue;
            }

            var atRest = Luminance(presenter.Background);

            ((IPseudoClasses)button.Classes).Set(":pointerover", true);
            Pump(220);
            var whileHovered = Luminance(presenter.Background);
            ((IPseudoClasses)button.Classes).Set(":pointerover", false);
            Pump(120);

            var box = placed.Bounds.TransformToAABB(placed.Transform);
            window.MouseDown(box.Center, MouseButton.Left);
            Pump(220);
            var whilePressed = Luminance(presenter.Background);
            window.MouseUp(box.Center, MouseButton.Left);
            window.Close();

            // Measured against hover, not rest: `tool` is fully transparent at
            // rest inside its rail, so any visible press is "brighter" than
            // nothing. Hover is the lit state a press actually recesses from.
            if (whilePressed > whileHovered + 0.005)
            {
                Faults.Add(
                    $"buttons: '{name}' gets brighter when pressed than when hovered " +
                    $"(hover {whileHovered:0.000} -> press {whilePressed:0.000}). A press must recess.");
            }
            else
            {
                Console.WriteLine(
                    $"  press '{name}': rest {atRest:0.000} / hover {whileHovered:0.000} / press {whilePressed:0.000}");
            }
        }
    }

    /// <summary>Perceived lightness of a fill, gradients averaged, alpha applied.</summary>
    private static double Luminance(IBrush? brush)
    {
        switch (brush)
        {
            case ISolidColorBrush solid:
                return Weight(solid.Color);
            case IGradientBrush gradient when gradient.GradientStops.Count > 0:
                return gradient.GradientStops.Average(stop => Weight(stop.Color));
            default:
                return 0;
        }

        // Alpha matters: these fills sit on a dark field, so a translucent
        // colour is darker in practice than its RGB suggests.
        static double Weight(Color color) =>
            ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0 * (color.A / 255.0);
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
        Action<Window>? afterSettle = null,
        string? expectVisibleText = null)
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

        // A shot exists to prove a surface renders; if the markup for that
        // surface is deleted (say, in a merge), the shot quietly becomes a
        // picture of the window behind it and proves nothing. Pinning one
        // expected string makes the disappearance a failure. This is exactly
        // how the Mainframe confirmation vanished unnoticed.
        if (expectVisibleText is not null)
        {
            var found = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Any(block => block.IsEffectivelyVisible && block.Text == expectVisibleText);
            if (!found)
            {
                Faults.Add($"{fileName}: expected visible text '{expectVisibleText}' was not rendered.");
            }
        }

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
