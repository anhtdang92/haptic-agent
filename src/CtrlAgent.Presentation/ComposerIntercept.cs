namespace CtrlAgent.Presentation;

/// <summary>What an intercepted composer line asks the UI to do.</summary>
public abstract record ComposerAction
{
    /// <summary>Bare "/model": show the model picker.</summary>
    public sealed record OpenModelPicker : ComposerAction;

    /// <summary>"/model x": set the model directly.</summary>
    public sealed record SetModel(string Model) : ComposerAction;
}

/// <summary>
/// Slash commands the app answers itself instead of sending to the agent.
/// <para>
/// In a terminal, "/model" opens the CLI's interactive picker. Headless
/// stream-json mode has no terminal UI, so the CLI can only reply "isn't
/// available in this environment" — a turn burned to tell the user to use a
/// mechanism they cannot see. The app HAS that mechanism (the model chip), so
/// typing the command routes there, the way the Claude desktop app intercepts
/// slash commands before sending. Everything else — including other slash
/// commands like "/compact", which the CLI genuinely handles headless — still
/// goes through untouched.
/// </para>
/// </summary>
public static class ComposerIntercept
{
    public static ComposerAction? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        const string command = "/model";
        if (!trimmed.StartsWith(command, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // "/models" or "/modelfoo" is some other command — not ours to answer.
        if (trimmed.Length > command.Length && !char.IsWhiteSpace(trimmed[command.Length]))
        {
            return null;
        }

        var argument = trimmed[command.Length..].Trim();
        return argument.Length == 0
            ? new ComposerAction.OpenModelPicker()
            : new ComposerAction.SetModel(argument);
    }
}
