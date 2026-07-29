using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace CtrlAgent.Gui;

/// <summary>
/// The fullscreen controller-first mode. All interaction flows through
/// <see cref="MainframeViewModel"/> — controller input via the engine's
/// capture path, keyboard as a desk-testing fallback.
/// </summary>
public sealed partial class MainframeWindow : Window
{
    private static bool _bootSequencePlayed;
    private MainframeViewModel? _observed;
    private double _windowedWidth = 1440;
    private double _windowedHeight = 860;

    /// <summary>Lets the app start dictation here when this window owns the
    /// screen, so a controller binding reaches the large voice overlay rather
    /// than quietly typing into a prompt box nobody can see.</summary>
    public void StartVoiceFromBinding() =>
        (DataContext as MainframeViewModel)?.StartVoiceFromBinding();

    public MainframeWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;

        // Enter sends while Shift+Enter inserts a line break. Tunnel because
        // AcceptsReturn causes the TextBox to consume Enter before bubbling.
        MainframePromptBox.AddHandler(
            KeyDownEvent,
            OnPromptKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        if (MainframeCursor.Reticle is { } reticle)
        {
            Cursor = reticle;
        }

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainframeViewModel viewModel && !ReferenceEquals(viewModel, _observed))
            {
                if (_observed is not null)
                {
                    _observed.FeedScrollRequested -= OnFeedScroll;
                    _observed.FocusMoved -= OnFocusMoved;
                    _observed.ControllerPressed -= SkipIntro;
                    _observed.Main.Transcript.CollectionChanged -= OnFeedChanged;
                    _observed.Main.TranscriptStreamed -= OnFeedStreamed;
                    _observed.Main.DiffJumpRequested -= OnDiffJump;
                }

                _observed = viewModel;
                viewModel.FocusMoved += OnFocusMoved;
                viewModel.FeedScrollRequested += OnFeedScroll;
                viewModel.ControllerPressed += SkipIntro;
                viewModel.Main.Transcript.CollectionChanged += OnFeedChanged;
                viewModel.Main.TranscriptStreamed += OnFeedStreamed;
                viewModel.Main.DiffJumpRequested += OnDiffJump;
            }
        };

        // Any pointer input dismisses the cinematic immediately. The intro is
        // atmosphere, never a gate between the user and their work.
        AddHandler(
            PointerPressedEvent,
            (_, _) => SkipIntro(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Opened += async (_, _) =>
        {
            // The full power-on sequence is memorable once, but repeat entries
            // should feel instant. Keep it once per app session and preserve the
            // existing input-to-skip behavior for the first showing.
            if (_bootSequencePlayed)
            {
                IntroOverlay.IsVisible = false;
                FocusPromptWhenReady();
                return;
            }

            _bootSequencePlayed = true;
            BootChime.Play();
            await Task.Delay(TimeSpan.FromSeconds(2.6));
            IntroOverlay.IsVisible = false;
            FocusPromptWhenReady();
        };
    }

    /// <summary>Moves keyboard users directly to the primary action after the
    /// intro clears without stealing focus while a modal surface is open.</summary>
    private void FocusPromptWhenReady()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (DataContext is MainframeViewModel viewModel &&
                    !viewModel.IsSettingsVisible &&
                    !viewModel.IsShortcutsVisible &&
                    !viewModel.IsVoiceVisible &&
                    !viewModel.IsSessionsVisible &&
                    !viewModel.Main.IsDiffVisible)
                {
                    MainframePromptBox.Focus();
                    MainframePromptBox.CaretIndex = MainframePromptBox.Text?.Length ?? 0;
                }
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Collapses the boot intro early — a keypress, click, or any
    /// controller button is a person saying "I'm here to work".</summary>
    private void SkipIntro()
    {
        if (IntroOverlay.IsVisible)
        {
            IntroOverlay.IsVisible = false;
            FocusPromptWhenReady();
        }
    }

    private void OnFeedScroll(int direction)
    {
        var scroller = DataContext is MainframeViewModel { Main.IsDiffVisible: true }
            ? MainframeDiffScroller
            : FeedScroller;
        if (scroller is null)
        {
            return;
        }

        var page = Math.Max(80, scroller.Viewport.Height * 0.8);
        var target = scroller.Offset.Y + (direction * page);
        var highest = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = scroller.Offset.WithY(Math.Clamp(target, 0, highest));
    }

    private void OnDiffJump(int rowIndex)
    {
        if (MainframeDiffList.ContainerFromIndex(rowIndex) is not Control container ||
            container.TranslatePoint(new Avalonia.Point(0, 0), MainframeDiffList) is not { } point)
        {
            return;
        }

        var highest = Math.Max(
            0,
            MainframeDiffScroller.Extent.Height - MainframeDiffScroller.Viewport.Height);
        MainframeDiffScroller.Offset = MainframeDiffScroller.Offset.WithY(
            Math.Clamp(point.Y, 0, highest));
    }

    private void OnFeedChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            FollowFeedIfAtBottom();
        }
    }

    private void OnFeedStreamed() => FollowFeedIfAtBottom();

    private void FollowFeedIfAtBottom()
    {
        if (FeedScroller is not { } scroller ||
            scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 48)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            scroller.ScrollToEnd,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnPromptKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter || eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        eventArgs.Handled = true;
        if (DataContext is MainframeViewModel viewModel)
        {
            viewModel.Main.SubmitPromptCommand.Execute(null);
            FocusPromptWhenReady();
        }
    }

    private void OnFocusMoved(int index)
    {
        if (TileRail.ContainerFromIndex(index) is Control container)
        {
            container.BringIntoView();
        }
    }

    private void OnTilePointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (DataContext is MainframeViewModel viewModel &&
            sender is Control { DataContext: MainframeTile tile })
        {
            viewModel.FocusTile(tile);
        }
    }

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (DataContext is MainframeViewModel viewModel &&
            sender is Control { DataContext: MainframeTile tile } &&
            eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            eventArgs.Handled = true;
            viewModel.ActivateTile(tile);
        }
    }

    private void OnToggleSettings(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as MainframeViewModel)?.ToggleSettings();

    private void OnMinimize(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        WindowState = WindowState.Minimized;

    private void OnToggleFullscreen(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            SystemDecorations = SystemDecorations.Full;
            WindowState = WindowState.Normal;
            Width = _windowedWidth;
            Height = _windowedHeight;
        }
        else
        {
            if (WindowState == WindowState.Normal)
            {
                _windowedWidth = Math.Max(960, Width);
                _windowedHeight = Math.Max(640, Height);
            }

            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.FullScreen;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        SkipIntro();
        if (DataContext is not MainframeViewModel viewModel)
        {
            return;
        }

        // Standard desktop affordances should work independently of the
        // controller navigation vocabulary.
        if (eventArgs.Key == Key.F11)
        {
            eventArgs.Handled = true;
            ToggleFullscreen();
            return;
        }

        if (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (eventArgs.Key)
            {
                case Key.L:
                    eventArgs.Handled = true;
                    FocusPromptWhenReady();
                    return;
                case Key.OemComma:
                    eventArgs.Handled = true;
                    viewModel.ToggleSettings();
                    return;
                case Key.M:
                    eventArgs.Handled = true;
                    WindowState = WindowState.Minimized;
                    return;
            }
        }

        var key = eventArgs.Key switch
        {
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Enter => "Enter",
            Key.Escape => "Escape",
            Key.Tab => "Tab",
            Key.F1 => "F1",
            Key.F2 => "F2",
            Key.F3 => "F3",
            Key.F4 => "F4",
            _ => null,
        };

        if (key is not null)
        {
            eventArgs.Handled = true;
            viewModel.OnKey(key);
        }
    }
}
