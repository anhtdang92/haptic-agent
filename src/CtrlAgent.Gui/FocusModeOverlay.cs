using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>
/// Adds the same Focus Contract selector and privacy-preserving attention
/// dashboard to Mission Control and Mainframe without duplicating window XAML.
/// </summary>
public static class FocusModeOverlay
{
    private static readonly ConditionalWeakTable<Window, object> Attached = new();

    public static void Attach(Window window, bool mainframe = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Attached.TryGetValue(window, out _) ||
            window.Content is not Control original || original.Parent is not null)
        {
            return;
        }
        Attached.Add(window, new object());

        var dashboard = new StackPanel
        {
            Spacing = 8,
            IsVisible = false,
            MinWidth = mainframe ? 340 : 300,
        };

        var modeText = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = mainframe ? 16 : 14,
        };
        var description = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = mainframe ? 420 : 360,
            Opacity = 0.78,
            FontSize = mainframe ? 13 : 12,
        };
        var workText = MetricLine();
        var protectedText = MetricLine();
        var decisionsText = MetricLine();
        var outcomesText = MetricLine();

        var cycle = new Button
        {
            MinWidth = mainframe ? 210 : 185,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8),
        };
        var expand = new Button
        {
            Content = "DETAILS",
            Padding = new Thickness(10, 8),
        };

        var modeButtons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = mainframe ? 145 : 130,
            ItemHeight = 38,
        };
        foreach (var mode in FocusContractSettings.Modes)
        {
            var captured = mode;
            var button = new Button
            {
                Content = FocusContractSettings.Label(mode),
                Margin = new Thickness(0, 0, 6, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => Select(captured);
            modeButtons.Children.Add(button);
        }

        dashboard.Children.Add(modeText);
        dashboard.Children.Add(description);
        dashboard.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
            Margin = new Thickness(0, 4),
        });
        dashboard.Children.Add(workText);
        dashboard.Children.Add(protectedText);
        dashboard.Children.Add(decisionsText);
        dashboard.Children.Add(outcomesText);
        dashboard.Children.Add(new TextBlock
        {
            Text = "Metrics contain no prompts, agent output, file paths, tool arguments, account data, or controller identifiers.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = mainframe ? 420 : 360,
            Opacity = 0.58,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
        });
        dashboard.Children.Add(modeButtons);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
        };
        header.Children.Add(cycle);
        header.Children.Add(expand);

        var cardBody = new StackPanel { Spacing = 10 };
        cardBody.Children.Add(header);
        cardBody.Children.Add(dashboard);

        var card = new Border
        {
            Background = new SolidColorBrush(mainframe
                ? Color.FromArgb(238, 8, 15, 25)
                : Color.FromArgb(232, 12, 20, 31)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 90, 220, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Margin = mainframe ? new Thickness(24) : new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = cardBody,
        };
        Panel.SetZIndex(card, 10000);

        var root = new Grid();
        root.Children.Add(original);
        root.Children.Add(card);
        window.Content = root;

        void RefreshMode(FocusContract contract)
        {
            var label = FocusContractSettings.Label(contract.Mode);
            cycle.Content = $"FOCUS · {label.ToUpperInvariant()}";
            modeText.Text = label;
            description.Text = FocusContractSettings.Description(contract.Mode);
        }

        void RefreshMetrics()
        {
            var snapshot = AttentionMetricsRegistry.Current.Snapshot();
            workText.Text = $"Autonomous work observed · {FormatDuration(snapshot.AutonomousWorkObserved)}";
            protectedText.Text = $"Routine interruptions suppressed · {snapshot.AvoidedRoutineInterruptions}";
            decisionsText.Text = $"Agent decisions handled · {snapshot.DecisionsHandled}";
            outcomesText.Text = $"Surfaced outcomes · {snapshot.CompletionsSurfaced} complete · {snapshot.ErrorsSurfaced} errors";
        }

        void Select(FocusMode mode)
        {
            GuiSettings.TrySaveFocusMode(mode);
            RefreshMode(FocusContractSettings.Current);
        }

        cycle.Click += (_, _) =>
        {
            var next = FocusContractSettings.Next();
            GuiSettings.TrySaveFocusMode(next);
        };
        expand.Click += (_, _) =>
        {
            dashboard.IsVisible = !dashboard.IsVisible;
            expand.Content = dashboard.IsVisible ? "HIDE" : "DETAILS";
            if (dashboard.IsVisible) RefreshMetrics();
        };

        void ContractChanged(FocusContract contract) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshMode(contract));
        void MetricsChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshMetrics);

        FocusContractSettings.Changed += ContractChanged;
        AttentionMetricsRegistry.Current.Changed += MetricsChanged;
        window.Closed += (_, _) =>
        {
            FocusContractSettings.Changed -= ContractChanged;
            AttentionMetricsRegistry.Current.Changed -= MetricsChanged;
        };

        RefreshMode(FocusContractSettings.Current);
        RefreshMetrics();
    }

    private static TextBlock MetricLine() => new()
    {
        FontSize = 12,
        Opacity = 0.88,
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }
        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }
        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }
}
