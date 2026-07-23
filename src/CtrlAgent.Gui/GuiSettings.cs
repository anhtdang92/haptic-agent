using System.Text.Json;

namespace CtrlAgent.Gui;

/// <summary>
/// Best-effort persistence of the last-used GUI options under
/// %AppData%/CtrlAgent/settings.json. Command-line arguments always win;
/// saved settings apply only when the app starts with no arguments.
/// </summary>
public sealed record GuiSettings(
    string? Agent,
    string? WorkingDirectory,
    string? DefaultPrompt,
    string? CodexPath,
    string? ClaudePath,
    string? GameInputBridgePath,
    string? ProfilePath)
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

    public static void TrySave(GuiOptions options)
    {
        try
        {
            var settings = new GuiSettings(
                options.Agent,
                options.WorkingDirectory,
                options.DefaultPrompt,
                options.CodexExecutable,
                options.ClaudeExecutable,
                options.GameInputBridgeExecutable,
                options.ProfilePath);

            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception)
        {
        }
    }

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
