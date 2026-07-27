using System.Diagnostics;
using System.Text;

namespace CtrlAgent.Presentation;

/// <summary>How a single diff line should render.</summary>
public enum DiffLineKind
{
    HunkHeader,
    Context,
    Added,
    Removed,
}

/// <summary>One rendered line of a file's diff, prefix included ("+", "-", " ")
/// so a monospace column reads exactly like git's own output.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>What happened to a file, as far as the diff header says.</summary>
public enum DiffFileChange
{
    Modified,
    Added,
    Deleted,
    Renamed,
}

/// <summary>
/// One file's worth of diff: where it is, what happened to it, and the lines
/// to show. <see cref="Lines"/> is empty for binary files — there is nothing
/// line-shaped to render, but the file still has to appear, because a binary
/// swap is exactly the kind of change worth noticing before an approval.
/// </summary>
public sealed record DiffFile(
    string Path,
    DiffFileChange Change,
    bool IsBinary,
    int Additions,
    int Deletions,
    IReadOnlyList<DiffLine> Lines,
    string? RenamedFrom = null)
{
    /// <summary>The file's one-line header: "M src/App.cs · +3 −1".</summary>
    public string Headline
    {
        get
        {
            var marker = Change switch
            {
                DiffFileChange.Added => "A",
                DiffFileChange.Deleted => "D",
                DiffFileChange.Renamed => "R",
                _ => "M",
            };
            var name = Change == DiffFileChange.Renamed && RenamedFrom is not null
                ? $"{RenamedFrom} → {Path}"
                : Path;
            var stats = IsBinary ? "binary" : $"+{Additions} −{Deletions}";
            return $"{marker} {name} · {stats}";
        }
    }
}

/// <summary>
/// Parses <c>git diff</c> unified output into files and lines.
/// <para>
/// This exists so the GUI can show the user what they are about to approve.
/// The whole product is "answer approvals from a controller", and until now
/// the answer button was reachable while the thing being approved was not
/// visible anywhere — you could approve an edit you had never seen. The
/// parser is pure (text in, records out) so the folding rules live where the
/// unit tests can reach them, same as <see cref="TranscriptFolder"/>.
/// </para>
/// </summary>
public static class UnifiedDiffParser
{
    public static IReadOnlyList<DiffFile> Parse(string? diffText)
    {
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return [];
        }

        var files = new List<DiffFile>();
        FileBuilder? current = null;

        foreach (var raw in diffText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush(files, current);
                current = new FileBuilder(PathFromHeader(line));
                continue;
            }

            if (current is null)
            {
                // Preamble before the first header (or not a diff at all).
                continue;
            }

            if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                current.Change = DiffFileChange.Added;
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                current.Change = DiffFileChange.Deleted;
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                current.RenamedFrom = line["rename from ".Length..];
                current.Change = DiffFileChange.Renamed;
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                current.Path = line["rename to ".Length..];
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                     line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                current.IsBinary = true;
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                // The header path can be quoted or mangled; the +++ path is
                // authoritative whenever it is a real file.
                current.Path = line["+++ b/".Length..];
            }
            else if (line.StartsWith("--- a/", StringComparison.Ordinal))
            {
                if (current.Change == DiffFileChange.Deleted)
                {
                    current.Path = line["--- a/".Length..];
                }
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current.InHunk = true;
                current.Lines.Add(new DiffLine(DiffLineKind.HunkHeader, line));
            }
            else if (current.InHunk)
            {
                if (line.StartsWith('+'))
                {
                    current.Additions++;
                    current.Lines.Add(new DiffLine(DiffLineKind.Added, line));
                }
                else if (line.StartsWith('-'))
                {
                    current.Deletions++;
                    current.Lines.Add(new DiffLine(DiffLineKind.Removed, line));
                }
                else if (!line.StartsWith('\\'))
                {
                    // "\ No newline at end of file" is noise; everything else
                    // inside a hunk is context.
                    current.Lines.Add(new DiffLine(DiffLineKind.Context, line));
                }
            }
        }

        Flush(files, current);
        return files;
    }

    private static void Flush(List<DiffFile> files, FileBuilder? builder)
    {
        if (builder is not null)
        {
            files.Add(builder.Build());
        }
    }

    /// <summary>Best-effort path from <c>diff --git a/x b/x</c>, used until a
    /// more authoritative line (+++/rename to) replaces it.</summary>
    private static string PathFromHeader(string line)
    {
        var rest = line["diff --git ".Length..];
        var marker = rest.LastIndexOf(" b/", StringComparison.Ordinal);
        return marker >= 0 ? rest[(marker + " b/".Length)..].Trim('"') : rest;
    }

    private sealed class FileBuilder(string path)
    {
        public string Path { get; set; } = path;

        public DiffFileChange Change { get; set; } = DiffFileChange.Modified;

        public bool IsBinary { get; set; }

        public bool InHunk { get; set; }

        public int Additions { get; set; }

        public int Deletions { get; set; }

        public List<DiffLine> Lines { get; } = [];

        public string? RenamedFrom { get; set; }

        public DiffFile Build() =>
            new(Path, Change, IsBinary, Additions, Deletions, Lines, RenamedFrom);
    }
}

/// <summary>
/// Everything changed in a workspace: tracked edits plus untracked files, or
/// the reason the question could not be answered.
/// </summary>
public sealed record WorkspaceChanges(
    IReadOnlyList<DiffFile> Files,
    string? Error = null,
    string? Note = null)
{
    public bool IsError => Error is not null;

    public int Additions => Files.Sum(file => file.Additions);

    public int Deletions => Files.Sum(file => file.Deletions);

    /// <summary>The one-line headline: "3 files · +120 −45".</summary>
    public string Summary
    {
        get
        {
            if (IsError)
            {
                return "Unavailable";
            }

            if (Files.Count == 0)
            {
                return "Working tree clean";
            }

            var noun = Files.Count == 1 ? "file" : "files";
            return $"{Files.Count} {noun} · +{Additions} −{Deletions}";
        }
    }
}

/// <summary>
/// Collects the workspace's pending changes by asking git.
/// <para>
/// Tracked changes come from <c>git diff HEAD</c> — staged and unstaged in one
/// view, because "what would I be approving" does not care about the index.
/// Untracked files are folded in as synthetic all-added diffs: a brand-new
/// file is the change most worth reading, and plain <c>git diff</c> never
/// shows it. Runs git rather than linking a library so the answer always
/// matches what the user's own <c>git status</c> says.
/// </para>
/// </summary>
public static class WorkspaceDiff
{
    /// <summary>Most lines rendered for one untracked file. Enough to read a
    /// new source file; a cap because "untracked" sometimes means a giant
    /// generated artifact that would drown the panel.</summary>
    private const int MaxUntrackedLines = 200;

    private const int MaxUntrackedFiles = 50;

    public static async Task<WorkspaceChanges> CollectAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        (int ExitCode, string StandardOutput, string StandardError) diff;
        try
        {
            diff = await RunGitAsync(directory, ["diff", "HEAD", "--no-color"], cancellationToken)
                .ConfigureAwait(false);
            if (diff.ExitCode != 0)
            {
                // A repository with no commits yet has no HEAD to diff against;
                // fall back to the index diff rather than showing an error for
                // a perfectly healthy new repo.
                var retry = await RunGitAsync(directory, ["diff", "--no-color"], cancellationToken)
                    .ConfigureAwait(false);
                if (retry.ExitCode != 0)
                {
                    return new WorkspaceChanges([], Error: FirstLine(diff.StandardError));
                }

                diff = retry;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new WorkspaceChanges([], Error: $"git could not run: {exception.Message}");
        }

        var files = new List<DiffFile>(UnifiedDiffParser.Parse(diff.StandardOutput));

        string? note = null;
        var untracked = await RunGitAsync(
            directory,
            ["ls-files", "--others", "--exclude-standard"],
            cancellationToken).ConfigureAwait(false);
        if (untracked.ExitCode == 0)
        {
            var paths = untracked.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            foreach (var path in paths.Take(MaxUntrackedFiles))
            {
                files.Add(ReadUntracked(directory, path));
            }

            if (paths.Length > MaxUntrackedFiles)
            {
                note = $"{paths.Length - MaxUntrackedFiles} more untracked files not shown";
            }
        }

        return new WorkspaceChanges(files, Note: note);
    }

    /// <summary>An untracked file rendered as the all-added diff it would be
    /// once tracked.</summary>
    private static DiffFile ReadUntracked(string directory, string relativePath)
    {
        try
        {
            var fullPath = Path.Combine(directory, relativePath);
            using var stream = File.OpenRead(fullPath);
            var probe = new byte[8192];
            var read = stream.Read(probe, 0, probe.Length);
            if (Array.IndexOf(probe, (byte)0, 0, read) >= 0)
            {
                return new DiffFile(relativePath, DiffFileChange.Added, IsBinary: true, 0, 0, []);
            }

            stream.Position = 0;
            using var reader = new StreamReader(stream);
            var lines = new List<DiffLine>();
            var total = 0;
            while (reader.ReadLine() is { } text)
            {
                total++;
                if (lines.Count < MaxUntrackedLines)
                {
                    lines.Add(new DiffLine(DiffLineKind.Added, "+" + text));
                }
            }

            if (total > MaxUntrackedLines)
            {
                lines.Add(new DiffLine(
                    DiffLineKind.HunkHeader,
                    $"@@ … {total - MaxUntrackedLines} more lines @@"));
            }

            return new DiffFile(relativePath, DiffFileChange.Added, IsBinary: false, total, 0, lines);
        }
        catch (IOException)
        {
            // The file vanished or is locked between ls-files and here; an
            // empty entry still tells the user it exists.
            return new DiffFile(relativePath, DiffFileChange.Added, IsBinary: false, 0, 0, []);
        }
        catch (UnauthorizedAccessException)
        {
            return new DiffFile(relativePath, DiffFileChange.Added, IsBinary: false, 0, 0, []);
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGitAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // -C instead of WorkingDirectory: Process.Start throws on a missing
        // working directory, while git -C reports it as an ordinary error
        // message we can show.
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(directory);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("git did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline >= 0 ? trimmed[..newline].TrimEnd('\r') : trimmed;
    }
}
