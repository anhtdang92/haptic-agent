using Avalonia.Media;
using Avalonia.Threading;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>
/// CTRL·BOT: the app's robotic companion. Its LED face mirrors the agent's
/// state and its speech bubble teaches the active profile's shortcuts —
/// rotating tips while idle, and the exact approve/decline inputs while an
/// approval is pending. Hints are rebuilt from the profile, so remapping in
/// the editor updates what the bot says. Must be used from the UI thread.
/// </summary>
public sealed class AgentBuddyViewModel : ViewModelBase
{
    private static readonly IBrush IdleAccent = new SolidColorBrush(Color.Parse("#2E6E8E"));
    private static readonly IBrush WorkingAccent = new SolidColorBrush(Color.Parse("#00D4FF"));
    private static readonly IBrush AlertAccent = new SolidColorBrush(Color.Parse("#FFB020"));
    private static readonly IBrush HappyAccent = new SolidColorBrush(Color.Parse("#34F5A4"));
    private static readonly IBrush ErrorAccent = new SolidColorBrush(Color.Parse("#FF5A78"));

    private static readonly string[] PokePhrases =
    [
        "Beep! That tickles ✨",
        "Ready when you are, boss!",
        "Boop 💙",
        "I live for green checkmarks.",
        "Squeeze a paddle, make my day.",
        "Charging happiness… 100%!",
    ];

    private readonly DispatcherTimer _tipTimer;
    private readonly List<string> _tips = [];
    private BotStats _stats = BotStats.TryLoad() ?? new BotStats(0, 0, 0, 0);
    private int _pokeIndex;
    private int _tipIndex;
    private string _message = "Booting up…";
    private string _approvalHint = "I need permission!";
    private string _workingHint = "On it…";
    private string _happyHint = "Done!";
    private bool _isIdle = true;
    private bool _isWorking;
    private bool _isAlert;
    private bool _isHappy;
    private bool _isError;
    private IBrush _accentBrush = IdleAccent;

    public AgentBuddyViewModel()
    {
        _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _tipTimer.Tick += (_, _) => AdvanceTip();
        _tipTimer.Start();
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public IBrush AccentBrush
    {
        get => _accentBrush;
        private set => Set(ref _accentBrush, value);
    }

    public bool IsIdle { get => _isIdle; private set => Set(ref _isIdle, value); }

    public bool IsWorking { get => _isWorking; private set => Set(ref _isWorking, value); }

    public bool IsAlert { get => _isAlert; private set => Set(ref _isAlert, value); }

    public bool IsHappy { get => _isHappy; private set => Set(ref _isHappy, value); }

    public bool IsError { get => _isError; private set => Set(ref _isError, value); }

    /// <summary>"LV 3" badge under the bot.</summary>
    public string LevelText => $"LV {_stats.Level}";

    /// <summary>Filled pixels of the 58px XP bar toward the next level.</summary>
    public double XpBarWidth => 58 * _stats.LevelProgress;

    public string StatsSummary =>
        $"Level {_stats.Level} · {_stats.Xp} XP — " +
        $"Prompts {_stats.Prompts} · Turns {_stats.Turns} · Approvals {_stats.Approvals} · Hiccups {_stats.Errors}";

    /// <summary>Clicking/tapping the bot: a happy reaction and a fresh line.</summary>
    public void Poke()
    {
        SetMood(happy: true, accent: HappyAccent);
        Message = PokePhrases[_pokeIndex++ % PokePhrases.Length];
    }

    /// <summary>A prompt went out (typed, voice, or button).</summary>
    public void CountPromptSent() => UpdateStats(_stats with { Prompts = _stats.Prompts + 1 });

    /// <summary>A pending approval was answered from anywhere.</summary>
    public void CountApprovalResolved() => UpdateStats(_stats with { Approvals = _stats.Approvals + 1 });

    private void UpdateStats(BotStats stats)
    {
        _stats = stats;
        stats.TrySave();
        Raise(nameof(LevelText));
        Raise(nameof(XpBarWidth));
        Raise(nameof(StatsSummary));
    }

    /// <summary>Rebuilds every hint from the profile's actual bindings.</summary>
    public void SetProfile(ControllerProfile profile)
    {
        _tips.Clear();

        AddTip(profile, AgentCommandKind.SubmitPrompt, "send me a prompt");
        AddTip(profile, AgentCommandKind.Interrupt, "interrupt me");
        AddTip(profile, AgentCommandKind.ReviewChanges, "ask me to review changes");
        AddTip(profile, AgentCommandKind.NewSession, "start a fresh session");
        AddTip(profile, AgentCommandKind.NextSession, "switch to the next session");
        AddTip(profile, AgentCommandKind.PreviousSession, "switch to the previous session");
        AddTip(profile, AgentCommandKind.ApproveOnce, "approve — but only while I'm asking");

        if (_tips.Count == 0)
        {
            _tips.Add("This profile has no bindings yet — open Edit profile…");
        }

        var approve = FindHint(profile, AgentCommandKind.ApproveOnce);
        var approveSession = FindHint(profile, AgentCommandKind.ApproveForSession);
        var decline = FindHint(profile, AgentCommandKind.Decline);
        _approvalHint = "I need permission! " +
            $"{approve ?? "?"} approve · {approveSession ?? "?"} approve session · {decline ?? "?"} decline";

        var interrupt = FindHint(profile, AgentCommandKind.Interrupt);
        _workingHint = interrupt is null ? "On it…" : $"On it… {interrupt} interrupts me.";

        var submit = FindHint(profile, AgentCommandKind.SubmitPrompt);
        _happyHint = submit is null ? "Done!" : $"Done! {submit} sends the next prompt.";

        _tipIndex = 0;
        if (IsIdle)
        {
            Message = _tips[0];
        }
    }

    public void OnAgentEvent(AgentEvent agentEvent)
    {
        switch (agentEvent.State)
        {
            case AgentStateKind.Working:
                SetMood(working: true, accent: WorkingAccent);
                Message = _workingHint;
                break;

            case AgentStateKind.ApprovalRequired:
            case AgentStateKind.WaitingForInput:
                SetMood(alert: true, accent: AlertAccent);
                Message = _approvalHint;
                break;

            case AgentStateKind.Completed:
                SetMood(happy: true, accent: HappyAccent);
                Message = _happyHint;
                UpdateStats(_stats with { Turns = _stats.Turns + 1 });
                break;

            case AgentStateKind.Error:
                SetMood(error: true, accent: ErrorAccent);
                Message = $"Uh-oh: {Truncate(agentEvent.Message)}";
                UpdateStats(_stats with { Errors = _stats.Errors + 1 });
                break;

            case AgentStateKind.Idle:
                SetMood(idle: true, accent: IdleAccent);
                Message = CurrentTip();
                break;
        }
    }

    private void AdvanceTip()
    {
        // The bot only chats tips while nothing dramatic is happening.
        if (!IsIdle && !IsHappy)
        {
            return;
        }

        if (IsHappy)
        {
            SetMood(idle: true, accent: IdleAccent);
        }

        _tipIndex++;
        Message = CurrentTip();
    }

    private string CurrentTip() =>
        _tips.Count == 0 ? "Ready." : _tips[_tipIndex % _tips.Count];

    private void SetMood(
        bool idle = false,
        bool working = false,
        bool alert = false,
        bool happy = false,
        bool error = false,
        IBrush? accent = null)
    {
        IsIdle = idle;
        IsWorking = working;
        IsAlert = alert;
        IsHappy = happy;
        IsError = error;
        AccentBrush = accent ?? IdleAccent;
    }

    private void AddTip(ControllerProfile profile, AgentCommandKind command, string friendly)
    {
        if (FindHint(profile, command) is { } hint)
        {
            _tips.Add($"{hint} → {friendly}");
        }
    }

    private static string? FindHint(ControllerProfile profile, AgentCommandKind command)
    {
        var binding = profile.Bindings.FirstOrDefault(candidate => candidate.Command == command);
        return binding is null ? null : ControlLabels.Chord(binding);
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "something went wrong — check the event stream.";
        }

        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= 90 ? singleLine : singleLine[..90] + "…";
    }
}
