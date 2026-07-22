namespace HapticAgent.App;

internal sealed record AppOptions(
    string Agent,
    string WorkingDirectory,
    string DefaultPrompt,
    string? CodexExecutable,
    bool Verbose)
{
    public static AppOptions Parse(string[] args)
    {
        var agent = "mock";
        var workingDirectory = Environment.CurrentDirectory;
        var defaultPrompt = "Inspect the current repository and continue implementing the highest-priority unfinished task.";
        string? codexExecutable = null;
        var verbose = false;

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
                case "--verbose":
                    verbose = true;
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
            !agent.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--agent must be either 'mock' or 'codex'.");
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        return new AppOptions(agent.ToLowerInvariant(), workingDirectory, defaultPrompt, codexExecutable, verbose);
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            HapticAgent console host

              --agent mock|codex   Agent adapter to use (default: mock)
              --cwd PATH           Agent working directory (default: current directory)
              --prompt TEXT        Prompt sent by the controller A button
              --codex-path PATH    Optional path to the Codex executable
              --verbose            Print analog controller changes
              --help               Show this help
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
