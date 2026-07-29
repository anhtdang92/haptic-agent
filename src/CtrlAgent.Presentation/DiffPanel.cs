namespace CtrlAgent.Presentation;

/// <summary>How one row of the diff review panel should render.</summary>
public enum DiffPanelRowKind
{
    FileHeader,
    HunkHeader,
    Added,
    Removed,
    Context,
    Elision,
}

/// <summary><see cref="DiffPanelRowKind.FileHeader"/> rows carry
/// <paramref name="IsFirstHeader"/> so the first file hugs the top of the
/// panel while later files get breathing room above their header.</summary>
public sealed record DiffPanelRow(DiffPanelRowKind Kind, string Text, bool IsFirstHeader);

/// <summary>Where one file's header row landed in the built row list —
/// the jump target for the file rail and the d-pad file step.</summary>
public sealed record DiffAnchor(int RowIndex, string Headline);

/// <summary>
/// Assembles a <see cref="WorkspaceChanges"/> into the flat row list the diff
/// panel renders, plus one anchor per rendered file header so navigation can
/// jump straight to a file. This used to live in the GUI's DiffRow, where the
/// cap/elision arithmetic and the header positions were untestable; the GUI
/// now only maps these rows to colors.
/// </summary>
public static class DiffPanel
{
    /// <summary>Everything past this many rows is elided behind one marker
    /// row. An ItemsControl will happily realize fifty thousand rows; the
    /// window will not survive it happily.</summary>
    public const int DefaultMaxRows = 4000;

    public static (IReadOnlyList<DiffPanelRow> Rows, IReadOnlyList<DiffAnchor> Anchors) Build(
        WorkspaceChanges changes, int maxRows = DefaultMaxRows)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var rows = new List<DiffPanelRow>();
        var anchors = new List<DiffAnchor>();
        var elided = 0;
        foreach (var file in changes.Files)
        {
            // A file whose header fell past the cap gets no anchor: a rail
            // entry that jumps nowhere is worse than an honest elision row.
            if (rows.Count < maxRows)
            {
                anchors.Add(new DiffAnchor(rows.Count, file.Headline));
            }

            AddCapped(rows, ref elided, maxRows,
                new DiffPanelRow(DiffPanelRowKind.FileHeader, file.Headline, rows.Count == 0));
            foreach (var line in file.Lines)
            {
                AddCapped(rows, ref elided, maxRows,
                    new DiffPanelRow(KindOf(line.Kind), line.Text, false));
            }
        }

        if (elided > 0)
        {
            rows.Add(new DiffPanelRow(
                DiffPanelRowKind.Elision, $"… {elided} more lines not shown", false));
        }

        return (rows, anchors);
    }

    private static void AddCapped(
        List<DiffPanelRow> rows, ref int elided, int maxRows, DiffPanelRow row)
    {
        if (rows.Count < maxRows)
        {
            rows.Add(row);
        }
        else
        {
            elided++;
        }
    }

    private static DiffPanelRowKind KindOf(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => DiffPanelRowKind.Added,
        DiffLineKind.Removed => DiffPanelRowKind.Removed,
        DiffLineKind.HunkHeader => DiffPanelRowKind.HunkHeader,
        _ => DiffPanelRowKind.Context,
    };
}
