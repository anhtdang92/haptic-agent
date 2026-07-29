using Avalonia;
using Avalonia.Media;
using CtrlAgent.Presentation;

namespace CtrlAgent.Gui;

/// <summary>
/// One rendered row of the diff review panel. Only the colours live here —
/// what the rows <em>say</em> (paths, counts, line kinds, ordering, the
/// elision cap, and where each file's header lands) comes from
/// <see cref="DiffPanel"/> in CtrlAgent.Presentation, where the tests can
/// reach it.
/// </summary>
public sealed class DiffRow
{
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#7DD3FC"));
    private static readonly IBrush HunkBrush = new SolidColorBrush(Color.Parse("#5A7099"));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#8CE8A8"));
    private static readonly IBrush AddedBackground = new SolidColorBrush(Color.Parse("#1434F5A4"));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.Parse("#FF9AA8"));
    private static readonly IBrush RemovedBackground = new SolidColorBrush(Color.Parse("#14FF5A78"));
    private static readonly IBrush ContextBrush = new SolidColorBrush(Color.Parse("#9FB7DF"));

    public required string Text { get; init; }

    public required IBrush Foreground { get; init; }

    public required IBrush Background { get; init; }

    public required FontWeight Weight { get; init; }

    public required Thickness Margin { get; init; }

    public static DiffRow From(DiffPanelRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.Kind switch
        {
            DiffPanelRowKind.FileHeader => new DiffRow
            {
                Text = row.Text,
                Foreground = HeaderBrush,
                Background = Brushes.Transparent,
                Weight = FontWeight.SemiBold,
                Margin = new Thickness(0, row.IsFirstHeader ? 0 : 12, 0, 2),
            },
            DiffPanelRowKind.Added => new DiffRow
            {
                Text = row.Text,
                Foreground = AddedBrush,
                Background = AddedBackground,
                Weight = FontWeight.Normal,
                Margin = default,
            },
            DiffPanelRowKind.Removed => new DiffRow
            {
                Text = row.Text,
                Foreground = RemovedBrush,
                Background = RemovedBackground,
                Weight = FontWeight.Normal,
                Margin = default,
            },
            DiffPanelRowKind.HunkHeader => new DiffRow
            {
                Text = row.Text,
                Foreground = HunkBrush,
                Background = Brushes.Transparent,
                Weight = FontWeight.Normal,
                Margin = new Thickness(0, 4, 0, 1),
            },
            DiffPanelRowKind.Elision => new DiffRow
            {
                Text = row.Text,
                Foreground = HunkBrush,
                Background = Brushes.Transparent,
                Weight = FontWeight.Normal,
                Margin = new Thickness(0, 8, 0, 0),
            },
            _ => new DiffRow
            {
                Text = row.Text,
                Foreground = ContextBrush,
                Background = Brushes.Transparent,
                Weight = FontWeight.Normal,
                Margin = default,
            },
        };
    }
}
