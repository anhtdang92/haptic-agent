namespace CtrlAgent.Core;

/// <summary>
/// Resolves a bare agent CLI name ("claude", "codex") to a launchable file path.
/// Windows process creation only finds .exe files on PATH, but npm-style installs
/// ship CLIs as .cmd shims, so adapters need a shell-style PATH + PATHEXT probe
/// before spawning. On platforms without PATHEXT the bare name is probed as-is.
/// </summary>
public static class AgentExecutableResolver
{
    private static readonly string[] LaunchableExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public static string Resolve(string name) =>
        Resolve(
            name,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            File.Exists);

    public static string Resolve(string name, string? pathValue, string? pathExtValue, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('/')
            || name.Contains('\\')
            || Path.IsPathRooted(name))
        {
            return name;
        }

        var candidates = BuildCandidates(name, pathExtValue);
        var directories = (pathValue ?? string.Empty).Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Directory-major search, matching how command shells resolve names.
        foreach (var directory in directories)
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (fileExists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return name;
    }

    private static IReadOnlyList<string> BuildCandidates(string name, string? pathExtValue)
    {
        if (string.IsNullOrWhiteSpace(pathExtValue))
        {
            return [name];
        }

        var candidates = new List<string>();
        if (Path.HasExtension(name))
        {
            candidates.Add(name);
        }

        foreach (var extension in pathExtValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Only extensions CreateProcess can actually launch; .ps1 and
            // friends on PATHEXT need a shell we do not want to spawn.
            foreach (var launchable in LaunchableExtensions)
            {
                if (string.Equals(extension, launchable, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(name + extension.ToLowerInvariant());
                    break;
                }
            }
        }

        return candidates;
    }
}
