namespace CtrlAgent.Gui;

public sealed record GuiOptions(
    string Agent,
    string WorkingDirectory,
    string DefaultPrompt,
    string? CodexExecutable,
    string? ClaudeExecutable,
    string? GameInputBridgeExecutable,
    string? ProfilePath)
{
    public static GuiOptions Parse(string[] args)
    {
        var agent = "mock";
        var workingDirectory = Environment.CurrentDirectory;
        var defaultPrompt = "Inspect the current repository and continue implementing the highest-priority unfinished task.";
        string? codexExecutable = null;
        string? claudeExecutable = null;
        string? gameInputBridgeExecutable = null;
        string? profilePath = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--agent":
                    agent = ReadValue(args, ref index, "--agent");
                    break;
                case "--cwd":
                    workingDirectory = Path.GetFullPath(ReadValue(args, ref index, "--cwd"));
                    break;
                case "--prompt":
                    defaultPrompt = ReadValue(args, ref index, "--prompt");
                    break;
                case "--codex-path":
                    codexExecutable = ReadValue(args, ref index, "--codex-path");
                    break;
                case "--claude-path":
                    claudeExecutable = ReadValue(args, ref index, "--claude-path");
                    break;
                case "--gameinput-bridge":
                    gameInputBridgeExecutable = Path.GetFullPath(ReadValue(args, ref index, "--gameinput-bridge"));
                    break;
                case "--profile":
                    profilePath = Path.GetFullPath(ReadValue(args, ref index, "--profile"));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        if (!agent.Equals("mock", StringComparison.OrdinalIgnoreCase) &&
            !agent.Equals("codex", StringComparison.OrdinalIgnoreCase) &&
            !agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--agent must be 'mock', 'codex', or 'claude'.");
        }

        return new GuiOptions(
            agent.ToLowerInvariant(),
            workingDirectory,
            defaultPrompt,
            codexExecutable,
            claudeExecutable,
            gameInputBridgeExecutable,
            profilePath);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }
}
