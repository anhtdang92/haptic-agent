using Avalonia;
using Avalonia.Media;
using CtrlAgent.Presentation;

namespace CtrlAgent.Gui;

/// <summary>
/// One rendered row of the diff review panel. Only the colours live here —
/// what the rows <em>say</em> (paths, counts, line kinds, truncation) comes
/// from <see cref="UnifiedDiffParser"/> and <see cref="DiffFile.Headline"/> in
/// CtrlAgent.Presentation, where the tests can reach it.
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

    /// <summary>Everything past this many rows is elided behind one marker
    /// row. An ItemsControl will happily realize fifty thousand rows; the
    /// window will not survive it happily.</summary>
    private const int MaxRows = 4000;

    public required string Text { get; init; }

    public required IBrush Foreground { get; init; }

    public required IBrush Background { get; init; }

    public required FontWeight Weight { get; init; }

    public required Thickness Margin { get; init; }

    public static IReadOnlyList<DiffRow> Build(WorkspaceChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var rows = new List<DiffRow>();
        var elided = 0;
        foreach (var file in changes.Files)
        {
            AddCapped(rows, ref elided, new DiffRow
            {
                Text = file.Headline,
                Foreground = HeaderBrush,
                Background = Brushes.Transparent,
                Weight = FontWeight.SemiBold,
                Margin = new Thickness(0, rows.Count == 0 ? 0 : 12, 0, 2),
            });

            foreach (var line in file.Lines)
            {
                AddCapped(rows, ref elided, From(line));
            }
        }

        if (elided > 0)
        {
            rows.Add(new DiffRow
            {
                Text = $"… {elided} more lines not shown",
                Foreground = HunkBrush,
                Background = Brushes.Transparent,
                Weight = FontWeight.Normal,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        return rows;
    }

    private static void AddCapped(List<DiffRow> rows, ref int elided, DiffRow row)
    {
        if (rows.Count < MaxRows)
        {
            rows.Add(row);
        }
        else
        {
            elided++;
        }
    }

    private static DiffRow From(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Added => new DiffRow
        {
            Text = line.Text,
            Foreground = AddedBrush,
            Background = AddedBackground,
            Weight = FontWeight.Normal,
            Margin = default,
        },
        DiffLineKind.Removed => new DiffRow
        {
            Text = line.Text,
            Foreground = RemovedBrush,
            Background = RemovedBackground,
            Weight = FontWeight.Normal,
            Margin = default,
        },
        DiffLineKind.HunkHeader => new DiffRow
        {
            Text = line.Text,
            Foreground = HunkBrush,
            Background = Brushes.Transparent,
            Weight = FontWeight.Normal,
            Margin = new Thickness(0, 4, 0, 1),
        },
        _ => new DiffRow
        {
            Text = line.Text,
            Foreground = ContextBrush,
            Background = Brushes.Transparent,
            Weight = FontWeight.Normal,
            Margin = default,
        },
    };
}
