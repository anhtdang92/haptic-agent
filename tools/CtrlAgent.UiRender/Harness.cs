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

    public static void Main()
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
        viewModel.SessionId = "sess-8f21c0a4";
        viewModel.PromptText = "Refactor the mapping engine tests";
        viewModel.AppendLog("Agent adapter 'claude' started.");
        viewModel.AppendLog("Controller connected: Xbox Elite Series 2");
        viewModel.AppendLog("[agent] Working: Bash: dotnet test");
        viewModel.AppendLog("[agent] Error: build failed in CtrlAgent.Core");
        viewModel.AppendLog("[agent] Completed: All tests pass (12.4s · 3 turns · $0.09)");

        Render(new MainWindow { DataContext = viewModel }, "01-main-window.png");

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

        var big = new MainframeViewModel(viewModel);
        Render(new MainframeWindow { DataContext = big }, "03-mainframe.png", 1600, 900,
            afterShow: w =>
            {
                // The boot intro is opaque until its animation finishes; the
                // harness clock does not advance, so retire it manually.
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
        Render(new ProfileEditorWindow(engine), "08-profile-editor.png", 900, 620);

        // Overlay HUD, with an approval pending. Built fresh: the shared
        // engine's completed turns clear the approval on older view models.
        var hud = new MainViewModel(engine, options);
        hud.AttachEngine(engine);
        hud.AgentState = "ApprovalRequired";
        hud.HasPendingApproval = true;
        hud.PendingApprovalMessage = "Claude Code wants: Write: src/CtrlAgent.Core/Mapping.cs";
        Render(new OverlayWindow { DataContext = hud }, "09-overlay.png", 380, 200);

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

        // The settings panel — the only place focus navigation exists now.
        var settings = new MainframeViewModel(viewModel);
        Render(new MainframeWindow { DataContext = settings }, "05-mainframe-settings.png", 1600, 900,
            afterShow: HideIntro,
            afterSettle: _ =>
            {
                settings.ToggleSettings();
                settings.OnKey("Right");
            });

        Console.WriteLine("done");
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
    private static void Pump(int milliseconds)
    {
        for (var elapsed = 0; elapsed < milliseconds; elapsed += 25)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(25);
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

        var frame = window.GetLastRenderedFrame();
        if (frame is null)
        {
            Console.WriteLine($"NO FRAME for {fileName}");
            return;
        }

        var path = Path.Combine(OutDir, fileName);
        frame.Save(path);
        Console.WriteLine($"wrote {path} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
        window.Close();
    }
}
