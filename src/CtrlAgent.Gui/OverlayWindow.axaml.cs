using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CtrlAgent.Gui;

/// <summary>
/// The compact always-on-top HUD: agent state, CTRL·BOT's coaching line, and
/// the approval actions. Shares the main window's view model so everything
/// stays in sync; the header row drags the window.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user asks to bring up the main window.</summary>
    public event Action? OpenMainRequested;

    /// <summary>Set by the app during shutdown so Close actually closes.</summary>
    public bool AllowClose { get; set; }

    // Fade in on every show (the window is hidden, not destroyed).
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            Opacity = change.GetNewValue<bool>() ? 1 : 0;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs eventArgs)
    {
        if (!AllowClose)
        {
            eventArgs.Cancel = true;
            Hide();
        }

        base.OnClosing(eventArgs);
    }

    private void OnDragStart(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void OnOpenMain(object? sender, RoutedEventArgs eventArgs) => OpenMainRequested?.Invoke();

    private void OnHide(object? sender, RoutedEventArgs eventArgs) => Hide();
}
