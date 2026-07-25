using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CtrlAgent.Core;
using CtrlAgent.Hosting;

namespace CtrlAgent.Gui;

/// <summary>One focusable tile on the Mainframe action rail.</summary>
public sealed class MainframeTile : ViewModelBase
{
    private bool _isFocused;

    public required string Id { get; init; }

    public required string Glyph { get; init; }

    public required string Label { get; init; }

    /// <summary>Tints approval tiles green/red so they read at a glance.</summary>
    public IBrush AccentBrush { get; init; } = new SolidColorBrush(Color.Parse("#7DD3FC"));

    public bool IsFocused
    {
        get => _isFocused;
        set => Set(ref _isFocused, value);
    }
}

/// <summary>
/// One shortcut shown on the Mainframe HUD: the button(s) that fire it and
/// what it does. Several bindings can drive the same command (the default
/// profile reaches every approval from both a paddle and an RB chord), so
/// hints merge those into one row — "P1 · RB+A" reads as one shortcut, where
/// two separate chips read as two different actions.
/// </summary>
public sealed class ActionHint
{
    public required string Chord { get; init; }

    public required string Label { get; init; }

    public required bool IsApproval { get; init; }
}

/// <summary>
/// The fullscreen mode: CTRL·BOT front and center with the agent's live
/// responses, a voice-prompt flow, and a fullscreen shortcuts screen.
/// <para>
/// Agent actions are never navigated. Submit, interrupt, review, approve,
/// decline and the rest fire from the user's own profile bindings exactly as
/// they do outside this mode — the screen shows those shortcuts rather than
/// offering buttons for them, so muscle memory is the same everywhere and a
/// destructive action can never be reached by wandering focus onto it.
/// </para>
/// <para>
/// Focus navigation exists only for settings. Input capture follows that
/// rule: it is enabled while the settings panel (or an overlay) is open, so
/// the d-pad moves focus instead of switching sessions, and disabled the rest
/// of the time so every binding stays live.
/// </para>
/// Must be used from the UI thread; engine events are marshaled here.
/// </summary>
public sealed class MainframeViewModel : ViewModelBase
{
    private static readonly IBrush ApproveAccent = new SolidColorBrush(Color.Parse("#34F5A4"));
    private static readonly IBrush DenyAccent = new SolidColorBrush(Color.Parse("#FF5A78"));

    private const int MaxResponses = 8;
    private const float StickEngage = 0.6f;
    private const float StickRelease = 0.4f;

    private readonly HostEngine _engine;
    private readonly SpeechToTextService _speech = new();
    private readonly Action<ControllerInputEvent> _inputHandler;
    private readonly Action<AgentEvent> _agentHandler;

    private int _focusIndex;
    private bool _stickLatched;
    private bool _isShortcutsVisible;
    private bool _isSettingsVisible;
    private string _hintHeading = "AVAILABLE NOW";
    private bool _isVoiceVisible;
    private bool _isListening;
    private bool _hasTranscript;
    private string _voiceStatus = string.Empty;
    private string _transcript = string.Empty;
    private string _latestResponse = "Waiting for the agent…";
    private bool _lastResponseWasWorking;
    private bool _isFeedEmpty = true;
    private bool _detached;

    public MainframeViewModel(MainViewModel main)
    {
        Main = main ?? throw new ArgumentNullException(nameof(main));
        _engine = main.Engine ?? throw new InvalidOperationException("Mainframe needs a running engine.");

        _inputHandler = inputEvent => Dispatcher.UIThread.Post(() => OnControllerInput(inputEvent));
        _agentHandler = agentEvent => Dispatcher.UIThread.Post(() => OnAgentEvent(agentEvent));
        _engine.ControllerInputReceived += _inputHandler;
        _engine.AgentEventReceived += _agentHandler;
        _engine.PendingApprovalChanged += OnPendingApprovalChanged;
        _speech.HypothesisChanged += text => Dispatcher.UIThread.Post(() =>
        {
            if (_isListening)
            {
                Transcript = text;
            }
        });

        // Capture stays off until something focusable is on screen.
        _engine.SetInputCapture(false);
        Main.PropertyChanged += OnMainPropertyChanged;
        RebuildTiles();
        RebuildHints();
    }

    /// <summary>Raised when the user asks to leave Mainframe mode.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the settings panel asks for the profile editor.</summary>
    public event Action? ProfileEditorRequested;

    /// <summary>Raised when the settings panel asks to change workspace.</summary>
    public event Action? WorkspacePickerRequested;

    /// <summary>Raised with the focused tile index so the rail can scroll it
    /// into view — the rail is wider than the screen once approval tiles
    /// join it.</summary>
    public event Action<int>? FocusMoved;

    public MainViewModel Main { get; }

    public ObservableCollection<MainframeTile> Tiles { get; } = [];

    /// <summary>The shortcuts that actually do something right now. Showing
    /// every binding at once buries the two that matter.</summary>
    public ObservableCollection<ActionHint> Hints { get; } = [];

    /// <summary>Headline over the HUD, naming the situation.</summary>
    public string HintHeading
    {
        get => _hintHeading;
        private set => Set(ref _hintHeading, value);
    }

    /// <summary>The agent's recent messages, oldest first.</summary>
    public ObservableCollection<string> Responses { get; } = [];

    public string LatestResponse
    {
        get => _latestResponse;
        private set => Set(ref _latestResponse, value);
    }

    /// <summary>True until the agent says something, so the feed can explain
    /// itself instead of showing an empty panel.</summary>
    public bool IsFeedEmpty
    {
        get => _isFeedEmpty;
        private set => Set(ref _isFeedEmpty, value);
    }

    public bool IsShortcutsVisible
    {
        get => _isShortcutsVisible;
        private set
        {
            if (Set(ref _isShortcutsVisible, value))
            {
                SyncCapture();
            }
        }
    }

    /// <summary>
    /// True while the settings panel is open. This is the only state in which
    /// focus navigation means anything, and the only state that captures
    /// controller input away from the profile.
    /// </summary>
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        private set
        {
            if (Set(ref _isSettingsVisible, value))
            {
                SyncCapture();
                Raise(nameof(IsHudVisible));
                Raise(nameof(LegendHint));
            }
        }
    }

    /// <summary>The action HUD hides behind any overlay.</summary>
    public bool IsHudVisible => !_isSettingsVisible && !_isShortcutsVisible && !_isVoiceVisible;

    /// <summary>
    /// The trailing legend line. It has to change with the mode: claiming
    /// "nothing to navigate" while the settings rail is on screen and taking
    /// d-pad input is simply untrue.
    /// </summary>
    public string LegendHint => _isSettingsVisible
        ? "Settings — d-pad moves, A selects, B closes"
        : "Every action above is a shortcut — nothing to navigate";

    public bool IsVoiceVisible
    {
        get => _isVoiceVisible;
        private set
        {
            if (Set(ref _isVoiceVisible, value))
            {
                SyncCapture();
            }
        }
    }

    public bool IsListening
    {
        get => _isListening;
        private set => Set(ref _isListening, value);
    }

    public string VoiceStatus
    {
        get => _voiceStatus;
        private set => Set(ref _voiceStatus, value);
    }

    public string Transcript
    {
        get => _transcript;
        private set => Set(ref _transcript, value);
    }

    /// <summary>Detaches from the engine; called when the window closes.</summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _engine.ControllerInputReceived -= _inputHandler;
        _engine.AgentEventReceived -= _agentHandler;
        _engine.PendingApprovalChanged -= OnPendingApprovalChanged;
        Main.PropertyChanged -= OnMainPropertyChanged;
        _engine.SetInputCapture(false);
        _speech.Dispose();
    }

    /// <summary>Keyboard fallback so the mode is testable without a pad.</summary>
    public void OnKey(string key)
    {
        switch (key)
        {
            case "Left": MoveFocus(-1); break;
            case "Right": MoveFocus(+1); break;
            case "Enter": Activate(); break;
            case "Escape": Back(); break;
            case "Tab": ToggleSettings(); break;
            case "F1": ToggleShortcuts(); break;
            case "F2": StartVoice(); break;
            case "F11": CloseRequested?.Invoke(); break;
        }
    }

    private void OnControllerInput(ControllerInputEvent inputEvent)
    {
        if (_detached)
        {
            return;
        }

        if (inputEvent.Kind == ControllerInputEventKind.ValueChanged)
        {
            HandleStick(inputEvent);
            return;
        }

        if (inputEvent.Kind != ControllerInputEventKind.Pressed)
        {
            return;
        }

        // View toggles settings from anywhere. Y opens voice. Everything else
        // is only ours while an overlay owns the screen — with no overlay up,
        // A/B/X and the paddles belong to the profile, and intercepting them
        // here would shadow the user's own bindings.
        if (inputEvent.Control == ControllerControl.View)
        {
            ToggleSettings();
            return;
        }

        if (inputEvent.Control == ControllerControl.Y && IsHudVisible)
        {
            StartVoice();
            return;
        }

        if (IsHudVisible)
        {
            return;
        }

        switch (inputEvent.Control)
        {
            case ControllerControl.DPadLeft: MoveFocus(-1); break;
            case ControllerControl.DPadRight: MoveFocus(+1); break;
            case ControllerControl.A: Activate(); break;
            case ControllerControl.B: Back(); break;
            case ControllerControl.Y: StartVoice(); break;
        }
    }

    private void HandleStick(ControllerInputEvent inputEvent)
    {
        if (inputEvent.Control != ControllerControl.LeftThumbstickX || IsHudVisible)
        {
            return;
        }

        var magnitude = Math.Abs(inputEvent.Value);
        if (_stickLatched)
        {
            if (magnitude < StickRelease)
            {
                _stickLatched = false;
            }

            return;
        }

        if (magnitude >= StickEngage)
        {
            _stickLatched = true;
            MoveFocus(Math.Sign(inputEvent.Value));
        }
    }

    private void MoveFocus(int direction)
    {
        if (Tiles.Count == 0 || IsVoiceVisible || !IsSettingsVisible)
        {
            return;
        }

        _focusIndex = ((_focusIndex + direction) % Tiles.Count + Tiles.Count) % Tiles.Count;
        SyncFocus();
    }

    private void Activate()
    {
        if (IsVoiceVisible)
        {
            ConfirmVoice();
            return;
        }

        if (IsShortcutsVisible)
        {
            IsShortcutsVisible = false;
            return;
        }

        if (Tiles.Count == 0)
        {
            return;
        }

        ActivateTile(Tiles[_focusIndex]);
    }

    /// <summary>
    /// Runs a settings tile. Public so a mouse click can take the same path
    /// the controller does — one activation route, not two.
    /// </summary>
    public void ActivateTile(MainframeTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        switch (tile.Id)
        {
            case "mode": Main.CyclePermissionModeCommand.Execute(null); break;
            case "workspace": WorkspacePickerRequested?.Invoke(); break;
            case "profile": ProfileEditorRequested?.Invoke(); break;
            case "shortcuts": ToggleShortcuts(); break;
            case "exit": CloseRequested?.Invoke(); break;
        }
    }

    private void Back()
    {
        if (IsVoiceVisible)
        {
            DismissVoice();
            return;
        }

        if (IsShortcutsVisible)
        {
            IsShortcutsVisible = false;
            return;
        }

        if (IsSettingsVisible)
        {
            IsSettingsVisible = false;
            return;
        }

        CloseRequested?.Invoke();
    }

    /// <summary>Opens or closes the settings panel. Public so the on-screen
    /// settings affordance can be clicked as well as pressed.</summary>
    public void ToggleSettings()
    {
        if (IsVoiceVisible)
        {
            return;
        }

        if (IsShortcutsVisible)
        {
            IsShortcutsVisible = false;
            return;
        }

        IsSettingsVisible = !IsSettingsVisible;
        if (IsSettingsVisible)
        {
            _focusIndex = 0;
            SyncFocus();
        }
    }

    /// <summary>Moves focus to a tile under the pointer, so hovering with a
    /// mouse and moving with the d-pad drive the same highlight.</summary>
    public void FocusTile(MainframeTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var index = Tiles.IndexOf(tile);
        if (index >= 0 && index != _focusIndex)
        {
            _focusIndex = index;
            SyncFocus();
        }
    }

    /// <summary>
    /// Capture is on exactly while something focusable owns the screen. Off
    /// otherwise, so the profile's bindings — including every agent action —
    /// stay live in the mode's resting state.
    /// </summary>
    private void SyncCapture()
    {
        if (_detached)
        {
            return;
        }

        _engine.SetInputCapture(_isSettingsVisible || _isShortcutsVisible || _isVoiceVisible);
    }

    private void ToggleShortcuts()
    {
        if (IsVoiceVisible)
        {
            return;
        }

        IsShortcutsVisible = !IsShortcutsVisible;
    }

    private async void StartVoice()
    {
        if (IsVoiceVisible || IsShortcutsVisible)
        {
            return;
        }

        IsVoiceVisible = true;
        _hasTranscript = false;
        Transcript = string.Empty;

        if (!_speech.EnsureInitialized())
        {
            IsListening = false;
            VoiceStatus = $"Voice input unavailable: {_speech.UnavailableReason ?? "unknown"} — press B to close.";
            return;
        }

        IsListening = true;
        VoiceStatus = "Listening… speak your prompt.";

        var text = await _speech.RecognizeOnceAsync().ConfigureAwait(true);
        if (!IsVoiceVisible)
        {
            return;
        }

        IsListening = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            VoiceStatus = "Nothing recognized — press Y to try again, B to close.";
            return;
        }

        _hasTranscript = true;
        Transcript = text;
        VoiceStatus = "Press A to send · Y to retry · B to discard.";
    }

    private void ConfirmVoice()
    {
        if (!_hasTranscript || string.IsNullOrWhiteSpace(Transcript))
        {
            return;
        }

        Main.SubmitPromptText(Transcript);
        DismissVoice();
    }

    private void DismissVoice()
    {
        _speech.CancelRecognition();
        IsVoiceVisible = false;
        IsListening = false;
        _hasTranscript = false;
        Transcript = string.Empty;
        VoiceStatus = string.Empty;
    }

    private void OnAgentEvent(AgentEvent agentEvent)
    {
        if (string.IsNullOrWhiteSpace(agentEvent.Message))
        {
            return;
        }

        // Streaming turns publish rolling Working snapshots; render them in
        // place (Claude-app style) instead of appending every snapshot.
        if (agentEvent.State == AgentStateKind.Working &&
            _lastResponseWasWorking &&
            Responses.Count > 0)
        {
            Responses[^1] = agentEvent.Message;
        }
        else
        {
            Responses.Add(agentEvent.Message);
            while (Responses.Count > MaxResponses)
            {
                Responses.RemoveAt(0);
            }
        }

        IsFeedEmpty = Responses.Count == 0;
        _lastResponseWasWorking = agentEvent.State == AgentStateKind.Working;
        LatestResponse = agentEvent.Message;
    }

    // Approvals are answered by paddles and chords, never by a tile, so a
    // pending request changes the HUD's emphasis rather than the rail.
    private void OnPendingApprovalChanged(string? message) =>
        Dispatcher.UIThread.Post(RebuildHints);

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        // Busy drives Interrupt's availability; the approval flag drives the
        // whole set. Both arrive on the UI thread already.
        if (eventArgs.PropertyName is nameof(MainViewModel.IsAgentActive)
            or nameof(MainViewModel.HasPendingApproval))
        {
            RebuildHints();
        }
    }

    /// <summary>
    /// Rebuilds the HUD for the current moment. Two rules:
    /// a binding appears only when it can actually fire, and while an approval
    /// is pending nothing but the approval answers is shown — that request is
    /// the only thing worth acting on until it is resolved.
    /// </summary>
    private void RebuildHints()
    {
        Hints.Clear();

        var pending = Main.HasPendingApproval;
        var busy = Main.IsAgentActive;
        HintHeading = pending ? "APPROVAL REQUIRED — ANSWER WITH" : "AVAILABLE NOW";

        // Merge bindings that drive the same command into one hint.
        var merged = new List<(AgentCommandKind Command, string Label, bool IsApproval, List<string> Chords)>();
        foreach (var binding in Main.Bindings)
        {
            if (!IsAvailable(binding, pending, busy))
            {
                continue;
            }

            var existing = merged.FindIndex(entry =>
                entry.Command == binding.Command && entry.Label == binding.Action);
            if (existing >= 0)
            {
                if (!merged[existing].Chords.Contains(binding.Chord))
                {
                    merged[existing].Chords.Add(binding.Chord);
                }
            }
            else
            {
                merged.Add((binding.Command, binding.Action, binding.IsApproval, [binding.Chord]));
            }
        }

        foreach (var entry in merged)
        {
            Hints.Add(new ActionHint
            {
                Chord = string.Join("  ·  ", entry.Chords),
                Label = entry.Label,
                IsApproval = entry.IsApproval,
            });
        }
    }

    /// <summary>
    /// Whether a binding can fire right now. Approval-gated bindings need a
    /// pending request; Interrupt needs something to interrupt; while a
    /// request is pending everything else steps aside.
    /// </summary>
    private static bool IsAvailable(BindingRow binding, bool pending, bool busy)
    {
        if (binding.IsApproval)
        {
            return pending;
        }

        if (pending)
        {
            return false;
        }

        return binding.Command != AgentCommandKind.Interrupt || busy;
    }

    /// <summary>
    /// While an approval is pending, the approval tiles lead the rail and
    /// take focus — the same priority the physical paddles get.
    /// </summary>
    private void RebuildTiles()
    {
        var focusedId = Tiles.Count > _focusIndex && Tiles.Count > 0 ? Tiles[_focusIndex].Id : null;
        Tiles.Clear();

        // Settings only. Agent actions deliberately have no tiles: they are
        // shortcuts, shown in the HUD, so nothing destructive is ever one
        // wandering d-pad press away.
        Tiles.Add(new MainframeTile { Id = "mode", Glyph = "🛡", Label = "Permission mode" });
        Tiles.Add(new MainframeTile { Id = "workspace", Glyph = "🗂", Label = "Workspace" });
        Tiles.Add(new MainframeTile { Id = "profile", Glyph = "⚙", Label = "Controller profile" });
        Tiles.Add(new MainframeTile { Id = "shortcuts", Glyph = "🎮", Label = "All shortcuts" });
        Tiles.Add(new MainframeTile { Id = "exit", Glyph = "⏏", Label = "Exit Mainframe" });

        _focusIndex = 0;
        if (focusedId is not null)
        {
            for (var index = 0; index < Tiles.Count; index++)
            {
                if (Tiles[index].Id == focusedId)
                {
                    _focusIndex = index;
                    break;
                }
            }
        }

        SyncFocus();
    }

    private void SyncFocus()
    {
        for (var index = 0; index < Tiles.Count; index++)
        {
            Tiles[index].IsFocused = index == _focusIndex;
        }

        FocusMoved?.Invoke(_focusIndex);
    }
}
