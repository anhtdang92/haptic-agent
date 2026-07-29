using System.Linq;
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
        SizeChanged += (_, _) => ApplyResponsiveLayout();

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
            ApplyResponsiveLayout();

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

    /// <summary>
    /// Keeps the conversation visually dominant at every supported window
    /// width. Large screens retain the controller cockpit; narrower windows
    /// reduce chrome, compact the status strip, and give the feed more room.
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        var shell = this.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(grid => grid.Classes.Contains("mainframeContent"));
        if (shell is null)
        {
            return;
        }

        var stage = shell.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 1 && grid.ColumnDefinitions.Count >= 2);
        var topStrip = shell.Children
            .OfType<DockPanel>()
            .FirstOrDefault(panel => Grid.GetRow(panel) == 0);

        var compact = Bounds.Width < 1600;
        var narrow = Bounds.Width < 1220;

        shell.Margin = narrow
            ? new Thickness(20, 16, 20, 16)
            : compact
                ? new Thickness(32, 22, 32, 20)
                : new Thickness(48, 28, 48, 24);

        if (stage is not null)
        {
            stage.Margin = narrow
                ? new Thickness(0, 10)
                : new Thickness(0, 14);

            // The original 2:3 split made supporting hardware UI nearly as
            // prominent as the work itself. Conversation now owns the canvas.
            stage.ColumnDefinitions[0].Width = narrow
                ? new GridLength(0.78, GridUnitType.Star)
                : compact
                    ? new GridLength(0.95, GridUnitType.Star)
                    : new GridLength(1.1, GridUnitType.Star);
            stage.ColumnDefinitions[1].Width = narrow
                ? new GridLength(2.22, GridUnitType.Star)
                : compact
                    ? new GridLength(2.15, GridUnitType.Star)
                    : new GridLength(2.4, GridUnitType.Star);

            var controllerHub = stage.Children
                .OfType<Grid>()
                .FirstOrDefault(grid => Grid.GetColumn(grid) == 0);
            if (controllerHub is not null)
            {
                controllerHub.MaxWidth = narrow ? 300 : compact ? 360 : 420;
            }

            var feedCard = stage.Children
                .OfType<Border>()
                .FirstOrDefault(border => Grid.GetColumn(border) == 1);
            if (feedCard is not null)
            {
                feedCard.Margin = new Thickness(narrow ? 14 : compact ? 18 : 24, 0, 0, 0);
                feedCard.Padding = new Thickness(narrow ? 18 : compact ? 21 : 24);
            }
        }

        if (topStrip?.Children
                .OfType<StackPanel>()
                .FirstOrDefault(panel => DockPanel.GetDock(panel) == Dock.Right) is { } statusItems)
        {
            statusItems.Spacing = narrow ? 12 : compact ? 16 : 24;

            // Profile, model and effort remain available in Settings. Hiding
            // their duplicate labels prevents the toolbar from wrapping or
            // competing with the live agent and workspace states.
            var secondaryLabels = statusItems.Children.OfType<TextBlock>().ToList();
            if (secondaryLabels.Count >= 4)
            {
                secondaryLabels[0].IsVisible = !compact;
                secondaryLabels[2].IsVisible = !compact;
                secondaryLabels[3].IsVisible = !compact;
            }
        }
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
