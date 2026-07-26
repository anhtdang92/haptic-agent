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
                    _observed.FocusMoved -= OnFocusMoved;
                }

                _observed = viewModel;
                viewModel.FocusMoved += OnFocusMoved;
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
            Key.Enter => "Enter",
            Key.Escape => "Escape",
            Key.Tab => "Tab",
            Key.F1 => "F1",
            Key.F2 => "F2",
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
