using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CtrlAgent.Core;
using CtrlAgent.Hosting;

namespace CtrlAgent.Gui;

/// <summary>One focusable tile on the Big Picture action rail.</summary>
public sealed class BigPictureTile : ViewModelBase
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
/// The Steam-Big-Picture-style fullscreen mode: a controller-navigated tile
/// rail (d-pad/left stick move focus, A selects, B backs out), CTRL·BOT
/// front and center with the agent's live responses, a voice-prompt flow,
/// and a fullscreen shortcuts screen. While this mode is open the host
/// engine captures controller input so presses drive the UI instead of
/// firing bindings — approval chords and paddles keep working throughout.
/// Must be used from the UI thread; engine events are marshaled here.
/// </summary>
public sealed class BigPictureViewModel : ViewModelBase
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
    private bool _isVoiceVisible;
    private bool _isListening;
    private bool _hasTranscript;
    private string _voiceStatus = string.Empty;
    private string _transcript = string.Empty;
    private string _latestResponse = "Waiting for the agent…";
    private bool _lastResponseWasWorking;
    private bool _detached;

    public BigPictureViewModel(MainViewModel main)
    {
        Main = main ?? throw new ArgumentNullException(nameof(main));
        _engine = main.Engine ?? throw new InvalidOperationException("Big Picture needs a running engine.");

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

        _engine.SetInputCapture(true);
        RebuildTiles();
    }

    /// <summary>Raised when the user asks to leave Big Picture mode.</summary>
    public event Action? CloseRequested;

    public MainViewModel Main { get; }

    public ObservableCollection<BigPictureTile> Tiles { get; } = [];

    /// <summary>The agent's recent messages, oldest first.</summary>
    public ObservableCollection<string> Responses { get; } = [];

    public string LatestResponse
    {
        get => _latestResponse;
        private set => Set(ref _latestResponse, value);
    }

    public bool IsShortcutsVisible
    {
        get => _isShortcutsVisible;
        private set => Set(ref _isShortcutsVisible, value);
    }

    public bool IsVoiceVisible
    {
        get => _isVoiceVisible;
        private set => Set(ref _isVoiceVisible, value);
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

        switch (inputEvent.Control)
        {
            case ControllerControl.DPadLeft: MoveFocus(-1); break;
            case ControllerControl.DPadRight: MoveFocus(+1); break;
            case ControllerControl.A: Activate(); break;
            case ControllerControl.B: Back(); break;
            case ControllerControl.X: ToggleShortcuts(); break;
            case ControllerControl.Y: StartVoice(); break;
        }
    }

    private void HandleStick(ControllerInputEvent inputEvent)
    {
        if (inputEvent.Control != ControllerControl.LeftThumbstickX)
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
        if (Tiles.Count == 0 || IsVoiceVisible)
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

        switch (Tiles[_focusIndex].Id)
        {
            case "voice": StartVoice(); break;
            case "submit": Main.SubmitPromptCommand.Execute(null); break;
            case "interrupt": Main.InterruptCommand.Execute(null); break;
            case "review": Main.ReviewCommand.Execute(null); break;
            case "compact": Main.SubmitPromptText("/compact"); break;
            case "newSession": Main.NewSessionCommand.Execute(null); break;
            case "mode": Main.CyclePermissionModeCommand.Execute(null); break;
            case "approveOnce": Main.ApproveOnceCommand.Execute(null); break;
            case "approveSession": Main.ApproveSessionCommand.Execute(null); break;
            case "decline": Main.DeclineCommand.Execute(null); break;
            case "cancel": Main.CancelCommand.Execute(null); break;
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

        CloseRequested?.Invoke();
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

        _lastResponseWasWorking = agentEvent.State == AgentStateKind.Working;
        LatestResponse = agentEvent.Message;
    }

    private void OnPendingApprovalChanged(string? message) =>
        Dispatcher.UIThread.Post(RebuildTiles);

    /// <summary>
    /// While an approval is pending, the approval tiles lead the rail and
    /// take focus — the same priority the physical paddles get.
    /// </summary>
    private void RebuildTiles()
    {
        var focusedId = Tiles.Count > _focusIndex && Tiles.Count > 0 ? Tiles[_focusIndex].Id : null;
        Tiles.Clear();

        var pending = Main.HasPendingApproval;
        if (pending)
        {
            Tiles.Add(new BigPictureTile { Id = "approveOnce", Glyph = "✓", Label = "Approve once", AccentBrush = ApproveAccent });
            Tiles.Add(new BigPictureTile { Id = "approveSession", Glyph = "✓✓", Label = "Approve session", AccentBrush = ApproveAccent });
            Tiles.Add(new BigPictureTile { Id = "decline", Glyph = "✗", Label = "Decline", AccentBrush = DenyAccent });
            Tiles.Add(new BigPictureTile { Id = "cancel", Glyph = "⊘", Label = "Cancel", AccentBrush = DenyAccent });
        }

        Tiles.Add(new BigPictureTile { Id = "voice", Glyph = "🎤", Label = "Voice prompt" });
        Tiles.Add(new BigPictureTile { Id = "submit", Glyph = "▶", Label = "Submit prompt" });
        Tiles.Add(new BigPictureTile { Id = "interrupt", Glyph = "⏹", Label = "Interrupt" });
        Tiles.Add(new BigPictureTile { Id = "review", Glyph = "🔍", Label = "Review changes" });
        Tiles.Add(new BigPictureTile { Id = "compact", Glyph = "🗜", Label = "Compact context" });
        Tiles.Add(new BigPictureTile { Id = "newSession", Glyph = "✚", Label = "New session" });
        Tiles.Add(new BigPictureTile { Id = "mode", Glyph = "🛡", Label = "Permission mode" });
        Tiles.Add(new BigPictureTile { Id = "shortcuts", Glyph = "🎮", Label = "Shortcuts" });
        Tiles.Add(new BigPictureTile { Id = "exit", Glyph = "⏏", Label = "Exit Big Picture" });

        _focusIndex = 0;
        if (pending)
        {
            // Land on Approve once when a request arrives.
        }
        else if (focusedId is not null)
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
    }
}
