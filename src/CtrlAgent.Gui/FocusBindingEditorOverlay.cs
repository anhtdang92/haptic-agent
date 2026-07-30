using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>
/// Exposes CtrlAgent's host-local Focus Mode binding beside ordinary agent
/// bindings. It is intentionally local: cycling attention policy must never be
/// sent to Claude, Codex, or another adapter as an agent command.
/// </summary>
public static class FocusBindingEditorOverlay
{
    public static void Attach(ProfileEditorWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Content is not Control original || original.Parent is not null)
        {
            return;
        }

        var current = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
        };
        var test = new Button
        {
            Content = "TEST / CYCLE",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 10, 19, 31)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(75, 90, 220, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel
            {
                Spacing = 7,
                Children =
                {
                    new TextBlock { Text = "HOST-LOCAL FOCUS BINDING", FontSize = 11, LetterSpacing = 1.5, Opacity = 0.65 },
                    current,
                    new TextBlock
                    {
                        Text = "Default gesture: squeeze both triggers past 80%. Release either below 50% before cycling again.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 360,
                        Opacity = 0.82,
                    },
                    new TextBlock
                    {
                        Text = "This action changes CtrlAgent's attention policy only. It never reaches the active AI adapter.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 360,
                        FontSize = 11,
                        Opacity = 0.58,
                    },
                    test,
                },
            },
        };

        var root = new Grid();
        root.Children.Add(original);
        root.Children.Add(card);
        window.Content = root;

        void Refresh(FocusContract contract) =>
            current.Text = $"Cycle Focus Mode · current: {FocusContractSettings.Label(contract.Mode)}";

        test.Click += (_, _) =>
        {
            var next = FocusContractSettings.Next();
            GuiSettings.TrySaveFocusMode(next);
        };

        FocusContractSettings.Changed += Refresh;
        window.Closed += (_, _) => FocusContractSettings.Changed -= Refresh;
        Refresh(FocusContractSettings.Current);
    }
}