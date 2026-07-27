namespace CtrlAgent.Presentation;

/// <summary>One styled span inside a markdown block.</summary>
public sealed record MarkdownRun(string Text, bool Bold = false, bool Italic = false, bool Code = false);

public abstract record MarkdownBlock;

/// <summary>Prose. Line breaks inside the source paragraph are preserved in
/// the run text.</summary>
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownRun> Runs) : MarkdownBlock;

/// <summary>A fenced code block. Renders monospaced, no inline styling.</summary>
public sealed record MarkdownCodeBlock(string Code, string? Language) : MarkdownBlock;

/// <summary>A single list item. Items are flat blocks (one per bullet) rather
/// than a nested tree — a chat transcript needs the visual rhythm of a list,
/// not full CommonMark nesting.</summary>
public sealed record MarkdownListItem(IReadOnlyList<MarkdownRun> Runs, string Marker) : MarkdownBlock;

/// <summary>A heading; level is 1–6.</summary>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownRun> Runs) : MarkdownBlock;

/// <summary>
/// The small slice of markdown that agent prose actually uses — fenced code,
/// `inline code`, **bold**, *italic*, bullets/numbered lists, headings —
/// parsed into blocks a UI can style. Deliberately not a CommonMark engine:
/// everything unrecognized stays literal text, so a false positive can only
/// ever look plain, never corrupt.
/// </summary>
public static class MarkdownLite
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var blocks = new List<MarkdownBlock>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new MarkdownParagraph(ParseInlines(string.Join('\n', paragraph))));
                paragraph.Clear();
            }
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var language = trimmed[3..].Trim();
                var body = new List<string>();
                index++;
                for (; index < lines.Length; index++)
                {
                    if (lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        break;
                    }

                    body.Add(lines[index]);
                }

                // An unterminated fence still renders as code: the author
                // clearly meant code, and the stream may simply not have
                // delivered the closing fence yet.
                blocks.Add(new MarkdownCodeBlock(
                    string.Join('\n', body),
                    language.Length == 0 ? null : language));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (TryParseHeading(trimmed, out var level, out var headingText))
            {
                FlushParagraph();
                blocks.Add(new MarkdownHeading(level, ParseInlines(headingText)));
                continue;
            }

            if (TryParseListItem(trimmed, out var marker, out var itemText))
            {
                FlushParagraph();
                blocks.Add(new MarkdownListItem(ParseInlines(itemText), marker));
                continue;
            }

            paragraph.Add(line);
        }

        FlushParagraph();
        return blocks;
    }

    private static bool TryParseHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level is < 1 or > 6 || level >= trimmed.Length || trimmed[level] != ' ')
        {
            return false;
        }

        text = trimmed[(level + 1)..].Trim();
        return text.Length > 0;
    }

    private static bool TryParseListItem(string trimmed, out string marker, out string text)
    {
        marker = string.Empty;
        text = string.Empty;

        if (trimmed.Length > 2 && trimmed[0] is '-' or '*' && trimmed[1] == ' ')
        {
            marker = "•";
            text = trimmed[2..].Trim();
            return text.Length > 0;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits is > 0 and <= 3 && digits + 1 < trimmed.Length &&
            trimmed[digits] == '.' && trimmed[digits + 1] == ' ')
        {
            marker = trimmed[..(digits + 1)];
            text = trimmed[(digits + 2)..].Trim();
            return text.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Splits prose into styled runs. Supported: <c>`code`</c> (wins over
    /// everything inside it), <c>**bold**</c>, and <c>*italic*</c> where the
    /// asterisks hug the content — <c>2 * 3 * 4</c> stays literal.
    /// </summary>
    public static IReadOnlyList<MarkdownRun> ParseInlines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var runs = new List<MarkdownRun>();
        ParseInlines(text, bold: false, italic: false, runs);
        return runs;
    }

    private static void ParseInlines(string text, bool bold, bool italic, List<MarkdownRun> runs)
    {
        var plainStart = 0;

        void FlushPlain(int end)
        {
            if (end > plainStart)
            {
                runs.Add(new MarkdownRun(text[plainStart..end], bold, italic));
            }
        }

        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];

            if (character == '`')
            {
                var close = text.IndexOf('`', index + 1);
                if (close > index + 1)
                {
                    FlushPlain(index);
                    runs.Add(new MarkdownRun(text[(index + 1)..close], bold, italic, Code: true));
                    index = close + 1;
                    plainStart = index;
                    continue;
                }
            }
            else if (character == '*')
            {
                var isDouble = index + 1 < text.Length && text[index + 1] == '*';
                var delimiter = isDouble ? "**" : "*";
                var contentStart = index + delimiter.Length;
                var close = FindEmphasisClose(text, contentStart, delimiter);
                if (close > contentStart)
                {
                    FlushPlain(index);
                    ParseInlines(
                        text[contentStart..close],
                        bold || isDouble,
                        italic || !isDouble,
                        runs);
                    index = close + delimiter.Length;
                    plainStart = index;
                    continue;
                }
            }

            index++;
        }

        FlushPlain(text.Length);
    }

    /// <summary>Finds the closing emphasis delimiter, requiring the content to
    /// hug it on both sides (no space just inside either end).</summary>
    private static int FindEmphasisClose(string text, int contentStart, string delimiter)
    {
        if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart]))
        {
            return -1;
        }

        var search = contentStart;
        while (true)
        {
            var close = text.IndexOf(delimiter, search, StringComparison.Ordinal);
            if (close <= contentStart)
            {
                return -1;
            }

            if (!char.IsWhiteSpace(text[close - 1]))
            {
                return close;
            }

            search = close + delimiter.Length;
        }
    }
}
