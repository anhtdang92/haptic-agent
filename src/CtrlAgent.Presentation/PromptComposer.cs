namespace CtrlAgent.Presentation;

/// <summary>
/// Folds attached file paths into the prompt that gets sent.
/// <para>
/// Attachments ride along with the next prompt rather than being sent on their
/// own. Sending them separately would burn a whole agent turn on "here is a
/// file" before the turn that says what to do with it, and the agent would have
/// to hold the reference across turns for no reason.
/// </para>
/// <para>
/// Paths are passed as text, not contents. Every agent this tool drives is a
/// coding agent sitting in the repository with file-reading tools — handing it
/// an absolute path is both smaller and more useful than pasting a file it can
/// open itself, and it keeps binary and huge files from wrecking the prompt.
/// </para>
/// </summary>
public static class PromptComposer
{
    public static string Compose(string? prompt, IReadOnlyList<string> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        var text = prompt?.Trim() ?? string.Empty;
        if (attachments.Count == 0)
        {
            return text;
        }

        var files = string.Join("\n", attachments.Select(path => $"- {path}"));
        var header = attachments.Count == 1 ? "Attached file:" : "Attached files:";

        // The instruction comes first when there is one, so the agent reads the
        // ask before the list rather than starting with bookkeeping.
        return text.Length == 0
            ? $"{header}\n{files}"
            : $"{text}\n\n{header}\n{files}";
    }
}
