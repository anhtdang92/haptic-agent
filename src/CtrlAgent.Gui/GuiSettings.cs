using System.Text.Json;

namespace CtrlAgent.Gui;

/// <summary>Window placement and view toggles saved between launches.</summary>
public sealed record UiState(
    bool ChatView,
    bool ShowControllerInput,
    double WindowWidth,
    double WindowHeight,
    int WindowX,
    int WindowY);

/// <summary>
/// Best-effort persistence of the last-used GUI options under
/// %AppData%/CtrlAgent/settings.json. Command-line arguments always win;
/// saved settings apply only when the app starts with no arguments. UI
/// state (view toggles, window placement) rides in the same file and is
/// preserved when only the options are re-saved.
/// </summary>
public sealed record GuiSettings(
    string? Agent,
    string? WorkingDirectory,
    string? DefaultPrompt,
    string? CodexPath,
    string? ClaudePath,
    string? GameInputBridgePath,
    string? ProfilePath,
    bool? ChatView = null,
    bool? ShowControllerInput = null,
    double? WindowWidth = null,
    double? WindowHeight = null,
    int? WindowX = null,
    int? WindowY = null,
    string[]? RecentWorkspaces = null,
    string? Microphone = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CtrlAgent",
        "settings.json");

    public static GuiSettings? TryLoad()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path), Options);
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings must never block startup.
            return null;
        }
    }

    public static void TrySave(GuiOptions options, UiState? uiState = null)
    {
        try
        {
            // Re-saving options alone must not wipe stored UI state, and the
            // workspace history has to survive every save path.
            var previous = TryLoad();
            var settings = new GuiSettings(
                options.Agent,
                options.WorkingDirectory,
                options.DefaultPrompt,
                options.CodexExecutable,
                options.ClaudeExecutable,
                options.GameInputBridgeExecutable,
                options.ProfilePath,
                uiState?.ChatView ?? previous?.ChatView,
                uiState?.ShowControllerInput ?? previous?.ShowControllerInput,
                uiState?.WindowWidth ?? previous?.WindowWidth,
                uiState?.WindowHeight ?? previous?.WindowHeight,
                uiState?.WindowX ?? previous?.WindowX,
                uiState?.WindowY ?? previous?.WindowY,
                Remember(previous?.RecentWorkspaces, options.WorkingDirectory),
                previous?.Microphone);

            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Persists the chosen capture device (null = system default)
    /// without disturbing anything else in the file.</summary>
    public static void TrySaveMicrophone(string? microphone)
    {
        try
        {
            var previous = TryLoad() ?? new GuiSettings(null, null, null, null, null, null, null);
            var settings = previous with { Microphone = microphone };
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception)
        {
        }
    }

    private const int MaxRecentWorkspaces = 8;

    /// <summary>
    /// Puts the directory at the front of the history, de-duplicated
    /// case-insensitively and capped. Directories that have since been deleted
    /// are dropped on the way past.
    /// </summary>
    private static string[] Remember(string[]? existing, string? directory)
    {
        var history = new List<string>();
        if (!string.IsNullOrWhiteSpace(directory))
        {
            history.Add(directory);
        }

        foreach (var entry in existing ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry) ||
                history.Any(known => string.Equals(known, entry, StringComparison.OrdinalIgnoreCase)) ||
                !Directory.Exists(entry))
            {
                continue;
            }

            history.Add(entry);
        }

        return [.. history.Take(MaxRecentWorkspaces)];
    }

    /// <summary>Stored workspaces that still exist, most recent first.</summary>
    public IReadOnlyList<string> UsableWorkspaces =>
        [.. (RecentWorkspaces ?? []).Where(Directory.Exists)];

    /// <summary>Merges saved values over the given defaults, dropping stale paths.</summary>
    public GuiOptions ApplyTo(GuiOptions defaults) => new(
        string.IsNullOrWhiteSpace(Agent) ? defaults.Agent : Agent.ToLowerInvariant(),
        Directory.Exists(WorkingDirectory) ? WorkingDirectory! : defaults.WorkingDirectory,
        string.IsNullOrWhiteSpace(DefaultPrompt) ? defaults.DefaultPrompt : DefaultPrompt,
        CodexPath,
        ClaudePath,
        File.Exists(GameInputBridgePath) ? GameInputBridgePath : null,
        File.Exists(ProfilePath) ? ProfilePath : null);
}
