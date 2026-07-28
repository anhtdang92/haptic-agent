namespace CtrlAgent.Core;

/// <summary>
/// Checks the things a session needs <em>before</em> the engine spawns
/// anything, and says what is wrong in words a first launch can act on.
/// <para>
/// Without this, every missing prerequisite surfaced as the raw exception of
/// whatever broke first: a deleted workspace was "The directory name is
/// invalid", a missing CLI was "The system cannot find the file specified" —
/// neither names the thing that is missing, and both arrived after the engine
/// was half-built. First launch is exactly when the user knows the least, so
/// the errors have to carry the most.
/// </para>
/// </summary>
public static class StartupPreflight
{
    /// <summary>Checks against the real filesystem and environment.</summary>
    public static IReadOnlyList<string> Check(
        string agent,
        string workingDirectory,
        string? agentExecutable,
        string? profilePath) =>
        Check(
            agent,
            workingDirectory,
            agentExecutable,
            profilePath,
            Directory.Exists,
            File.Exists,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"));

    /// <summary>
    /// Pure overload: every probe injected, so tests can stage any machine.
    /// Returns one readable problem per missing prerequisite; empty means go.
    /// </summary>
    public static IReadOnlyList<string> Check(
        string agent,
        string workingDirectory,
        string? agentExecutable,
        string? profilePath,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        string? searchPath,
        string? pathExtensions)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(fileExists);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(workingDirectory) || !directoryExists(workingDirectory))
        {
            problems.Add(
                $"The workspace folder does not exist: {workingDirectory}. " +
                "Pick another folder — it may have been moved or deleted since last time.");
        }

        if (profilePath is not null && !fileExists(profilePath))
        {
            problems.Add($"The controller profile file does not exist: {profilePath}.");
        }

        var install = agent switch
        {
            "claude" => "npm install -g @anthropic-ai/claude-code",
            "codex" => "npm install -g @openai/codex",
            _ => null,
        };
        if (install is null)
        {
            // The mock agent spawns nothing; there is nothing to preflight.
            return problems;
        }

        if (agentExecutable is not null)
        {
            if (!CanLaunch(agentExecutable, searchPath, pathExtensions, fileExists))
            {
                problems.Add($"The {agent} executable does not exist: {agentExecutable}.");
            }
        }
        else if (!CanLaunch(agent, searchPath, pathExtensions, fileExists))
        {
            problems.Add(
                $"The {agent} CLI was not found on PATH. Install it with: {install} — " +
                $"then run '{agent}' once in a terminal to sign in before using CtrlAgent.");
        }

        return problems;
    }

    /// <summary>
    /// Whether a command would actually start: explicit paths are checked
    /// directly, bare names through the same PATH/PATHEXT probe the adapters
    /// launch with (<see cref="AgentExecutableResolver"/>) — so the preflight
    /// can never pass a name the spawn would then fail on, or vice versa.
    /// </summary>
    private static bool CanLaunch(
        string command,
        string? searchPath,
        string? pathExtensions,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        if (command.Contains('/') || command.Contains('\\') || Path.IsPathRooted(command))
        {
            return fileExists(command);
        }

        // The resolver hands back the input unchanged exactly when nothing on
        // PATH matched.
        return !string.Equals(
            AgentExecutableResolver.Resolve(command, searchPath, pathExtensions, fileExists),
            command,
            StringComparison.Ordinal);
    }
}
