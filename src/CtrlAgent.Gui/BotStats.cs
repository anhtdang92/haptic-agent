using System.Text.Json;

namespace CtrlAgent.Gui;

/// <summary>
/// CTRL·BOT's tamagotchi ledger: how much work bot and user have done
/// together. Persisted best-effort to %AppData%/CtrlAgent/botstats.json so
/// the bot keeps its level across launches; failures never surface.
/// </summary>
public sealed record BotStats(int Prompts, int Turns, int Approvals, int Errors)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string StatsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CtrlAgent",
        "botstats.json");

    /// <summary>Turns weigh most; even errors earn a sympathy point.</summary>
    public int Xp => (Prompts * 2) + (Turns * 5) + (Approvals * 3) + Errors;

    /// <summary>Level n starts at 8·(n−1)² XP, so early levels come fast.</summary>
    public int Level => 1 + (int)Math.Sqrt(Xp / 8.0);

    /// <summary>0..1 progress from the current level to the next.</summary>
    public double LevelProgress
    {
        get
        {
            var lower = 8 * (Level - 1) * (Level - 1);
            var upper = 8 * Level * Level;
            return Math.Clamp((Xp - lower) / (double)(upper - lower), 0d, 1d);
        }
    }

    public static BotStats? TryLoad()
    {
        try
        {
            var path = StatsPath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<BotStats>(File.ReadAllText(path), Options)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void TrySave()
    {
        try
        {
            var path = StatsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
        }
    }
}
