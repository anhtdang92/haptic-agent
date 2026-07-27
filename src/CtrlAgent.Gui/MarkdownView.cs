using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using CtrlAgent.Presentation;

namespace CtrlAgent.Gui;

/// <summary>
/// Renders agent prose as lightweight markdown (via
/// <see cref="MarkdownLite"/>): bold/italic/inline code inside wrapped text,
/// fenced code in monospaced panels, bullets and headings. Code-built rather
/// than templated because the block list is heterogeneous and tiny; a full
/// markdown package would drag in a dependency for five block types.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,monospace");
    private static readonly IBrush CodeForeground = Brush.Parse("#9FE8FF");
    private static readonly IBrush CodeBackground = Brush.Parse("#1A9FE8FF");
    private static readonly IBrush CodeBlockBackground = Brush.Parse("#33060B14");
    private static readonly IBrush BulletForeground = Brush.Parse("#7FDBFF");

    public MarkdownView()
    {
        Spacing = 6;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var block in MarkdownLite.Parse(Markdown))
        {
            Children.Add(block switch
            {
                MarkdownCodeBlock code => BuildCodeBlock(code),
                MarkdownHeading heading => BuildHeading(heading),
                MarkdownListItem item => BuildListItem(item),
                MarkdownParagraph paragraph => BuildRuns(paragraph.Runs, fontSize: 12.5),
                _ => new TextBlock(),
            });
        }
    }

    private static Control BuildCodeBlock(MarkdownCodeBlock code) => new Border
    {
        Background = CodeBlockBackground,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8),
        Child = new SelectableTextBlock
        {
            Text = code.Code,
            FontFamily = MonoFont,
            FontSize = 11.5,
            Foreground = CodeForeground,
            TextWrapping = TextWrapping.Wrap,
        },
    };

    private static Control BuildHeading(MarkdownHeading heading)
    {
        var text = BuildRuns(heading.Runs, fontSize: heading.Level switch
        {
            1 => 16,
            2 => 14.5,
            _ => 13,
        });
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = new Thickness(0, heading.Level <= 2 ? 4 : 2, 0, 0);
        return text;
    }

    private static Control BuildListItem(MarkdownListItem item)
    {
        var marker = new TextBlock
        {
            Text = item.Marker,
            FontSize = 12.5,
            Foreground = BulletForeground,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 0, 8, 0),
        };

        var body = BuildRuns(item.Runs, fontSize: 12.5);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(marker, Dock.Left);
        row.Children.Add(marker);
        row.Children.Add(body);
        return row;
    }

    private static SelectableTextBlock BuildRuns(IReadOnlyList<MarkdownRun> runs, double fontSize)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            Foreground = Brush.Parse("#E2EDFF"),
        };

        var inlines = new InlineCollection();
        foreach (var run in runs)
        {
            var inline = new Run(run.Text);
            if (run.Code)
            {
                inline.FontFamily = MonoFont;
                inline.Foreground = CodeForeground;
                inline.Background = CodeBackground;
            }

            if (run.Bold)
            {
                inline.FontWeight = FontWeight.Bold;
            }

            if (run.Italic)
            {
                inline.FontStyle = FontStyle.Italic;
            }

            inlines.Add(inline);
        }

        text.Inlines = inlines;
        return text;
    }
}
