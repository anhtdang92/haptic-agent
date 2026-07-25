using Avalonia.Controls;
using Avalonia.Input;

namespace CtrlAgent.Gui;

/// <summary>
/// The fullscreen controller-first mode. All interaction flows through
/// <see cref="BigPictureViewModel"/> — controller input via the engine's
/// capture path, keyboard as a desk-testing fallback.
/// </summary>
public sealed partial class BigPictureWindow : Window
{
    public BigPictureWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Opened += async (_, _) =>
        {
            // Steam-style boot moment: chime + badge/ring animation, then the
            // overlay collapses out of the way of input rendering.
            BootChime.Play();
            await Task.Delay(TimeSpan.FromSeconds(2.6));
            IntroOverlay.IsVisible = false;
        };
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
