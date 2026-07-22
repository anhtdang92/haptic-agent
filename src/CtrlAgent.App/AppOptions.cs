namespace CtrlAgent.App;

internal sealed record AppOptions(
    string Agent,
    string WorkingDirectory,
    string DefaultPrompt,
    string? CodexExecutable,
    string? ClaudeExecutable,
    string? GameInputBridgeExecutable,
    string? ProfilePath,
    string? ExportProfilePath,
    bool Verbose,
    bool Validate)
{
    public static AppOptions Parse(string[] args)
    {
        var agent = "mock";
        var workingDirectory = Environment.CurrentDirectory;
        var defaultPrompt = "Inspect the current repository and continue implementing the highest-priority unfinished task.";
        string? codexExecutable = null;
        string? claudeExecutable = null;
        string? gameInputBridgeExecutable = null;
        string? profilePath = null;
        string? exportProfilePath = null;
        var verbose = false;
        var validate = false;

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
                    gameInputBridgeExecutable = Path.GetFullPath(
                        ReadValue(args, ref index, "--gameinput-bridge"));
                    break;
                case "--profile":
                    profilePath = Path.GetFullPath(ReadValue(args, ref index, "--profile"));
                    break;
                case "--export-profile":
                    exportProfilePath = Path.GetFullPath(ReadValue(args, ref index, "--export-profile"));
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--validate":
                    validate = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
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

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        if (profilePath is not null && !File.Exists(profilePath))
        {
            throw new FileNotFoundException($"Profile file does not exist: {profilePath}", profilePath);
        }

        return new AppOptions(
            agent.ToLowerInvariant(),
            workingDirectory,
            defaultPrompt,
            codexExecutable,
            claudeExecutable,
            gameInputBridgeExecutable,
            profilePath,
            exportProfilePath,
            verbose,
            validate);
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            CtrlAgent console host

              --agent mock|codex|claude  Agent adapter to use (default: mock)
              --cwd PATH              Agent working directory (default: current directory)
              --prompt TEXT           Prompt sent by the controller A button
              --codex-path PATH       Optional path to the Codex executable
              --claude-path PATH      Optional path to the Claude Code executable
              --gameinput-bridge PATH Optional path to CtrlAgent.GameInputBridge.exe
              --profile PATH          Load a controller profile from a JSON file
              --export-profile PATH   Write the default profile as JSON and exit
              --validate              Run the guided hardware validation wizard
              --verbose               Print analog controller changes
              --help                  Show this help
            """);
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
