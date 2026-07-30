using System.Text.Json;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

public sealed record UiState(
    bool ShowControllerInput,
    double WindowWidth,
    double WindowHeight,
    int WindowX,
    int WindowY);

public sealed record GuiSettings(
    string? Agent,
    string? WorkingDirectory,
    string? DefaultPrompt,
    string? CodexPath,
    string? ClaudePath,
    string? GameInputBridgePath,
    string? ProfilePath,
    bool? ShowControllerInput = null,
    double? WindowWidth = null,
    double? WindowHeight = null,
    int? WindowX = null,
    int? WindowY = null,
    string[]? RecentWorkspaces = null,
    string? Microphone = null,
    string? SpeechProvider = null,
    string? OpenAiSpeechModel = null,
    string? WhisperExecutable = null,
    string? WhisperModel = null,
    string? SpeechLanguage = null,
    string? FocusMode = null)
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
            return File.Exists(path)
                ? JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path), Options)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void TrySave(GuiOptions options, UiState? uiState = null)
    {
        try
        {
            var previous = TryLoad();
            var settings = new GuiSettings(
                options.Agent,
                options.WorkingDirectory,
                options.DefaultPrompt,
                options.CodexExecutable,
                options.ClaudeExecutable,
                options.GameInputBridgeExecutable,
                options.ProfilePath,
                uiState?.ShowControllerInput ?? previous?.ShowControllerInput,
                uiState?.WindowWidth ?? previous?.WindowWidth,
                uiState?.WindowHeight ?? previous?.WindowHeight,
                uiState?.WindowX ?? previous?.WindowX,
                uiState?.WindowY ?? previous?.WindowY,
                Remember(previous?.RecentWorkspaces, options.WorkingDirectory),
                previous?.Microphone,
                previous?.SpeechProvider,
                previous?.OpenAiSpeechModel,
                previous?.WhisperExecutable,
                previous?.WhisperModel,
                previous?.SpeechLanguage,
                previous?.FocusMode);
            Write(settings);
        }
        catch (Exception) { }
    }

    public static void TrySaveMicrophone(string? microphone)
    {
        try
        {
            var previous = TryLoad() ?? new GuiSettings(null, null, null, null, null, null, null);
            Write(previous with { Microphone = microphone });
        }
        catch (Exception) { }
    }

    public static void ApplySpeechSettings()
    {
        var settings = TryLoad();
        if (settings is null)
        {
            return;
        }

        if (Enum.TryParse<SpeechProviderKind>(settings.SpeechProvider, true, out var provider))
        {
            SpeechProviderSettings.Provider = provider;
        }
        if (!string.IsNullOrWhiteSpace(settings.OpenAiSpeechModel))
        {
            SpeechProviderSettings.OpenAiModel = settings.OpenAiSpeechModel;
        }
        SpeechProviderSettings.WhisperExecutable = settings.WhisperExecutable;
        SpeechProviderSettings.WhisperModel = settings.WhisperModel;
        SpeechToTextService.Language = settings.SpeechLanguage;
    }

    public static void TrySaveSpeechSettings(
        SpeechProviderKind provider,
        string? openAiModel,
        string? whisperExecutable,
        string? whisperModel,
        string? language)
    {
        try
        {
            var previous = TryLoad() ?? new GuiSettings(null, null, null, null, null, null, null);
            Write(previous with
            {
                SpeechProvider = provider.ToString(),
                OpenAiSpeechModel = openAiModel,
                WhisperExecutable = whisperExecutable,
                WhisperModel = whisperModel,
                SpeechLanguage = language,
            });
        }
        catch (Exception) { }
    }

    public static void ApplyFocusSettings()
    {
        var settings = TryLoad();
        if (Enum.TryParse<FocusMode>(settings?.FocusMode, true, out var mode))
        {
            FocusContractSettings.Select(mode);
        }
    }

    public static void TrySaveFocusMode(FocusMode mode)
    {
        try
        {
            var previous = TryLoad() ?? new GuiSettings(null, null, null, null, null, null, null);
            Write(previous with { FocusMode = mode.ToString() });
            FocusContractSettings.Select(mode);
        }
        catch (Exception) { }
    }

    private static void Write(GuiSettings settings)
    {
        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }

    private const int MaxRecentWorkspaces = 8;

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

    public IReadOnlyList<string> UsableWorkspaces =>
        [.. (RecentWorkspaces ?? []).Where(Directory.Exists)];

    public GuiOptions ApplyTo(GuiOptions defaults) => new(
        string.IsNullOrWhiteSpace(Agent) ? defaults.Agent : Agent.ToLowerInvariant(),
        Directory.Exists(WorkingDirectory) ? WorkingDirectory! : defaults.WorkingDirectory,
        string.IsNullOrWhiteSpace(DefaultPrompt) ? defaults.DefaultPrompt : DefaultPrompt,
        CodexPath,
        ClaudePath,
        File.Exists(GameInputBridgePath) ? GameInputBridgePath : null,
        File.Exists(ProfilePath) ? ProfilePath : null);
}
