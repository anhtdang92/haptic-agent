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

/// <summary>Stand-in for the Windows-only dictation service.</summary>
public sealed class SpeechToTextService : IDisposable
{
    public event Action<string>? HypothesisChanged;

    public string? UnavailableReason { get; private set; } = "headless harness";

    public bool EnsureInitialized()
    {
        HypothesisChanged?.Invoke(string.Empty);
        return false;
    }

    public Task<string?> RecognizeOnceAsync() => Task.FromResult<string?>(null);

    public void CancelRecognition() { }

    public void Dispose() { }
}

/// <summary>The real App.axaml styles with a headless-safe code-behind.</summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // The window code-behind calls these; the harness renders windows directly.
    public void ShowBigPicture() { }

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

        var big = new BigPictureViewModel(viewModel);
        Render(new BigPictureWindow { DataContext = big }, "03-big-picture.png", 1600, 900,
            afterShow: w =>
            {
                // The boot intro is opaque until its animation finishes; the
                // harness clock does not advance, so retire it manually.
                if (w.FindControl<Border>("IntroOverlay") is { } intro)
                {
                    intro.IsVisible = false;
                }
            });

        var bigApproval = new BigPictureViewModel(approving);
        Render(new BigPictureWindow { DataContext = bigApproval }, "04-big-picture-approval.png", 1600, 900,
            afterShow: w =>
            {
                if (w.FindControl<Border>("IntroOverlay") is { } intro)
                {
                    intro.IsVisible = false;
                }
            });

        // Verify the rail scrolls: walk focus to the far end with the d-pad.
        var bigScrolled = new BigPictureViewModel(approving);
        Render(new BigPictureWindow { DataContext = bigScrolled }, "05-rail-scrolled.png", 1600, 900,
            afterShow: w =>
            {
                if (w.FindControl<Border>("IntroOverlay") is { } intro)
                {
                    intro.IsVisible = false;
                }
            },
            afterSettle: _ =>
            {
                for (var i = 0; i < 12; i++)
                {
                    bigScrolled.OnKey("Right");
                }
            });

        Console.WriteLine("done");
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

        // Let layout, bindings, and the first render frame settle.
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        if (afterSettle is not null)
        {
            afterSettle(window);
            for (var i = 0; i < 30; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(25);
            }
        }

        Dispatcher.UIThread.RunJobs();
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
