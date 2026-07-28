using System.Text.Json;

namespace CtrlAgent.Adapters.ClaudeCode;

/// <summary>A session found in Claude Code's on-disk store: enough to render
/// a recents list and to resume the session by id.</summary>
public sealed record ClaudeSessionInfo(
    string SessionId,
    string Title,
    DateTimeOffset LastActivity);

/// <summary>One prose message recovered from a stored transcript.</summary>
public sealed record ClaudeTranscriptEntry(bool IsUser, string Text);

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

    /// <summary>
    /// Loads a stored session's conversation so a UI can show what was said
    /// before resuming it. Without this, resuming an old session showed an
    /// empty transcript: the CLI reloads its context from this same file, so
    /// the agent remembered a conversation the user could not see.
    /// <para>
    /// Prose only — user prompts and assistant text. Tool calls, tool results,
    /// thinking blocks, and synthetic user entries (command echoes and system
    /// reminders, which arrive wrapped in angle brackets) are skipped: the
    /// point is rereading the conversation, not replaying the machinery.
    /// Only the last <paramref name="maxEntries"/> messages are kept, matching
    /// what a transcript view can usefully hold.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ClaudeTranscriptEntry> LoadTranscript(
        string workspaceDirectory,
        string sessionId,
        string? claudeHome = null,
        int maxEntries = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var home = claudeHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var path = Path.Combine(
            home, "projects", EncodeProjectDirectoryName(workspaceDirectory), sessionId + ".jsonl");

        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return ParseTranscript(ReadAllLines(reader), maxEntries);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        static IEnumerable<string> ReadAllLines(StreamReader reader)
        {
            while (reader.ReadLine() is { } line)
            {
                yield return line;
            }
        }
    }

    /// <summary>Pure core of <see cref="LoadTranscript"/>, split out so tests
    /// can feed lines directly.</summary>
    public static IReadOnlyList<ClaudeTranscriptEntry> ParseTranscript(
        IEnumerable<string> transcriptLines,
        int maxEntries = 200)
    {
        ArgumentNullException.ThrowIfNull(transcriptLines);

        var entries = new List<ClaudeTranscriptEntry>();
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
                    case "user":
                        if (TryReadUserText(root, out var prompt))
                        {
                            entries.Add(new ClaudeTranscriptEntry(IsUser: true, prompt));
                        }

                        break;

                    case "assistant":
                        if (TryReadAssistantText(root, out var reply))
                        {
                            entries.Add(new ClaudeTranscriptEntry(IsUser: false, reply));
                        }

                        break;
                }
            }
        }

        return entries.Count <= maxEntries ? entries : entries[^maxEntries..];
    }

    /// <summary>Assistant prose: the "text" blocks of the message, joined.
    /// Thinking and tool_use blocks are machinery, not conversation.</summary>
    private static bool TryReadAssistantText(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty("type", out var blockType) &&
                blockType.ValueKind == JsonValueKind.String &&
                blockType.GetString() == "text" &&
                block.TryGetProperty("text", out var blockText) &&
                blockText.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(blockText.GetString()))
            {
                parts.Add(blockText.GetString()!.Trim());
            }
        }

        if (parts.Count == 0)
        {
            return false;
        }

        text = string.Join("\n\n", parts);
        return true;
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
