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
    /// <summary>Lets the app start dictation here when this window owns the
    /// screen, so a controller binding reaches the large voice overlay rather
    /// than quietly typing into a prompt box nobody can see.</summary>
    public void StartVoiceFromBinding() =>
        (DataContext as MainframeViewModel)?.StartVoiceFromBinding();

    private MainframeViewModel? _observed;

    public MainframeWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;

        // Same Enter-sends contract as the main window's prompt box, and for
        // the same reason: AcceptsReturn means the TextBox consumes Enter
        // before any bubbling handler sees it, so the submit hook must tunnel.
        MainframePromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // The reticle pointer: this mode is meant to be driven from a couch,
        // but a mouse should feel deliberate here rather than borrowed from
        // the desktop. Null means the backend refused it — keep the default.
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
                    _observed.Responses.CollectionChanged -= OnFeedChanged;
                }

                _observed = viewModel;
                viewModel.FocusMoved += OnFocusMoved;
                viewModel.FeedScrollRequested += OnFeedScroll;
                viewModel.Responses.CollectionChanged += OnFeedChanged;
            }
        };
        Opened += async (_, _) =>
        {
            // Steam-style boot moment: chime + badge/ring animation, then the
            // overlay collapses out of the way of input rendering.
            BootChime.Play();
            await Task.Delay(TimeSpan.FromSeconds(2.6));
            IntroOverlay.IsVisible = false;
        };
    }

    /// <summary>
    /// Pages the agent feed from a controller binding.
    /// <para>
    /// Pages rather than lines: a flick of the stick should move a readable
    /// amount, and line-by-line scrolling from across a room is unusable. The
    /// page is a fraction of the viewport so a couple of lines carry over and
    /// you can tell where you were.
    /// </para>
    /// </summary>
    private void OnFeedScroll(int direction)
    {
        // The surface on top gets the stick: the diff overlay while it is
        // open, the conversation feed otherwise.
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

    /// <summary>
    /// Follows new and streaming feed text while already at the bottom, same
    /// rules as the main window's conversation: stickiness is decided before
    /// layout absorbs the change, the scroll runs after it, and paging back
    /// with the stick disarms following until the user returns to the bottom.
    /// Replace matters as much as Add here — a streaming turn rewrites the
    /// last row in place.
    /// </summary>
    private void OnFeedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action is not (System.Collections.Specialized.NotifyCollectionChangedAction.Add
            or System.Collections.Specialized.NotifyCollectionChangedAction.Replace))
        {
            return;
        }

        if (FeedScroller is not { } scroller ||
            scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 48)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            scroller.ScrollToEnd,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    // Enter sends, Shift+Enter breaks the line — the habit every chat client
    // teaches, kept identical across both windows.
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
        }
    }

    /// <summary>Keeps the focused settings tile on screen.</summary>
    private void OnFocusMoved(int index)
    {
        if (TileRail.ContainerFromIndex(index) is Control container)
        {
            container.BringIntoView();
        }
    }

    /// <summary>Hovering a settings tile moves focus to it, so the pointer and
    /// the d-pad drive the same highlight rather than competing ones.</summary>
    private void OnTilePointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (DataContext is MainframeViewModel viewModel &&
            sender is Control { DataContext: MainframeTile tile })
        {
            viewModel.FocusTile(tile);
        }
    }

    /// <summary>Clicking a settings tile runs it — the same path A takes.</summary>
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

    private void OnToggleSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as MainframeViewModel)?.ToggleSettings();

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not MainframeViewModel viewModel)
        {
            return;
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
            Key.F11 => "F11",
            _ => null,
        };

        if (key is not null)
        {
            eventArgs.Handled = true;
            viewModel.OnKey(key);
        }
    }
}
