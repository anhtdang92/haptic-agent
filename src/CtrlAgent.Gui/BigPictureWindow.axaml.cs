using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace CtrlAgent.Gui;

/// <summary>
/// The fullscreen controller-first mode. All interaction flows through
/// <see cref="BigPictureViewModel"/> — controller input via the engine's
/// capture path, keyboard as a desk-testing fallback.
/// </summary>
public sealed partial class BigPictureWindow : Window
{
    private BigPictureViewModel? _observed;

    public BigPictureWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is BigPictureViewModel viewModel && !ReferenceEquals(viewModel, _observed))
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

    /// <summary>
    /// Keeps the focused tile on screen: the rail is wider than the display
    /// once the four approval tiles join it, so focus must drag the viewport.
    /// </summary>
    private void OnFocusMoved(int index)
    {
        if (TileRail.ContainerFromIndex(index) is Control container)
        {
            container.BringIntoView();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not BigPictureViewModel viewModel)
        {
            return;
        }

        var key = eventArgs.Key switch
        {
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Enter => "Enter",
            Key.Escape => "Escape",
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
