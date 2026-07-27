using System.Text.Json;

namespace CtrlAgent.Adapters.ClaudeCode;

/// <summary>A session found in Claude Code's on-disk store: enough to render
/// a recents list and to resume the session by id.</summary>
public sealed record ClaudeSessionInfo(
    string SessionId,
    string Title,
    DateTimeOffset LastActivity);

/// <summary>
/// Reads Claude Code's on-disk session store so a UI can show a recents list
/// like the Claude app's sidebar. The CLI keeps one JSONL transcript per
/// session under <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;session-id&gt;.jsonl</c>,
/// where the encoded name is the workspace path with every character outside
/// [A-Za-z0-9] replaced by <c>-</c>.
/// <para>
/// This reads the store directly rather than asking the CLI because no wire
/// command lists past sessions — the store on disk is the only inventory of
/// them, and it is also what <c>--resume</c> reads. The format is observed
/// from real transcripts (CLI 2.1.x), not documented, so parsing is
/// deliberately tolerant: a line that is not JSON or has an unexpected shape
/// is skipped, and a transcript that yields no title still lists (as
/// "New session") rather than hiding a session that exists.
/// </para>
/// </summary>
public static class ClaudeSessionCatalog
{
    // A transcript can be tens of MB; the title is in the first user message,
    // which sits near the top under a bounded preamble (queue operations,
    // file-history snapshots). Cap how far we look so listing stays cheap.
    private const int TitleScanLineLimit = 250;

    /// <summary>Maximum characters of the first prompt used as the title.</summary>
    private const int TitleLengthLimit = 80;

    /// <summary>Lists sessions for a workspace, newest activity first.</summary>
    public static IReadOnlyList<ClaudeSessionInfo> ListSessions(string workspaceDirectory, string? claudeHome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);

        var home = claudeHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var projectDirectory = Path.Combine(home, "projects", EncodeProjectDirectoryName(workspaceDirectory));

        if (!Directory.Exists(projectDirectory))
        {
            return [];
        }

        var sessions = new List<ClaudeSessionInfo>();
        foreach (var file in Directory.EnumerateFiles(projectDirectory, "*.jsonl"))
        {
            try
            {
                var title = ExtractTitle(ReadLeadingLines(file)) ?? "New session";
                sessions.Add(new ClaudeSessionInfo(
                    Path.GetFileNameWithoutExtension(file),
                    title,
                    File.GetLastWriteTimeUtc(file)));
            }
            catch (IOException)
            {
                // The CLI may hold the live transcript open; a session we
                // cannot read right now is skipped, not fatal.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        sessions.Sort((left, right) => right.LastActivity.CompareTo(left.LastActivity));
        return sessions;
    }

    /// <summary>The store's directory name for a workspace path: every
    /// character outside [A-Za-z0-9] becomes '-'.</summary>
    public static string EncodeProjectDirectoryName(string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);

        var trimmed = workspaceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                span[index] = char.IsAsciiLetterOrDigit(character) ? character : '-';
            }
        });
    }

    /// <summary>
    /// Derives a session title from transcript lines: a stored summary line
    /// wins (the CLI writes one when a session is continued or compacted),
    /// otherwise the first real user prompt. Synthetic user entries — command
    /// echoes and system reminders, which arrive wrapped in angle brackets —
    /// do not become titles.
    /// </summary>
    public static string? ExtractTitle(IEnumerable<string> transcriptLines)
    {
        ArgumentNullException.ThrowIfNull(transcriptLines);

        string? firstPrompt = null;
        foreach (var line in transcriptLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var typeProperty) ||
                    typeProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                switch (typeProperty.GetString())
                {
                    case "summary":
                        if (root.TryGetProperty("summary", out var summary) &&
                            summary.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(summary.GetString()))
                        {
                            return Shorten(summary.GetString()!);
                        }

                        break;

                    case "user":
                        if (firstPrompt is null &&
                            TryReadUserText(root, out var prompt))
                        {
                            firstPrompt = Shorten(prompt);
                        }

                        break;
                }
            }
        }

        return firstPrompt;
    }

    private static bool TryReadUserText(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content))
        {
            return false;
        }

        // Content is either a plain string or an array of blocks whose text
        // parts carry a "text" property.
        string? candidate = null;
        if (content.ValueKind == JsonValueKind.String)
        {
            candidate = content.GetString();
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("text", out var blockText) &&
                    blockText.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(blockText.GetString()))
                {
                    candidate = blockText.GetString();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(candidate) || candidate.TrimStart().StartsWith('<'))
        {
            return false;
        }

        text = candidate;
        return true;
    }

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= TitleLengthLimit ? single : single[..TitleLengthLimit].TrimEnd() + "…";
    }

    private static IEnumerable<string> ReadLeadingLines(string path)
    {
        // FileShare.ReadWrite: the CLI appends to the live session's file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        for (var count = 0; count < TitleScanLineLimit; count++)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }
}
