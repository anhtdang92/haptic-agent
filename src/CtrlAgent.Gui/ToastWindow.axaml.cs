using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace CtrlAgent.Gui;

/// <summary>
/// In-app notification card shown at the bottom-right of the primary work
/// area while the main window is hidden. Auto-dismisses; the approval variant
/// carries Approve/Decline buttons; clicking the body opens the main window.
/// (Deliberately not WinRT toasts: no Action Center, but zero dependencies
/// and it only matters while the tray app is running anyway.)
/// </summary>
public sealed partial class ToastWindow : Window
{
    private readonly DispatcherTimer _autoClose;

    public ToastWindow()
    {
        InitializeComponent();
        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _autoClose.Tick += (_, _) => Close();
        Opened += (_, _) =>
        {
            PositionBottomRight();
            _autoClose.Start();
        };
        Closed += (_, _) => _autoClose.Stop();
    }

    public event Action? ApproveRequested;

    public event Action? DeclineRequested;

    public event Action? OpenRequested;

    public void Configure(string title, string message, string accentHex, bool showApprovalButtons)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        AccentDot.Fill = new SolidColorBrush(Color.Parse(accentHex));
        ApprovalButtons.IsVisible = showApprovalButtons;
    }

    private void PositionBottomRight()
    {
        if (Screens.Primary?.WorkingArea is { } area)
        {
            var height = Math.Max((int)Bounds.Height, 90);
            Position = new PixelPoint(area.Right - (int)Width - 24, area.Bottom - height - 24);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs eventArgs) => Close();

    private void OnApprove(object? sender, RoutedEventArgs eventArgs)
    {
        ApproveRequested?.Invoke();
        Close();
    }

    private void OnDecline(object? sender, RoutedEventArgs eventArgs)
    {
        DeclineRequested?.Invoke();
        Close();
    }

    private void OnBodyPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        OpenRequested?.Invoke();
        Close();
    }
}
