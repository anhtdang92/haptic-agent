using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CtrlAgent.Core;
using CtrlAgent.Hosting;
using CtrlAgent.Presentation;

namespace CtrlAgent.Gui;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute(parameter);
}

public sealed class MainViewModel : ViewModelBase
{
    private const int MaxLogEntries = 400;

    private static readonly IBrush DotGood = new SolidColorBrush(Color.Parse("#34F5A4"));
    private static readonly IBrush DotBusy = new SolidColorBrush(Color.Parse("#00D4FF"));
    private static readonly IBrush DotWarn = new SolidColorBrush(Color.Parse("#FFB020"));
    private static readonly IBrush DotBad = new SolidColorBrush(Color.Parse("#FF5A78"));
    private static readonly IBrush DotOff = new SolidColorBrush(Color.Parse("#5A7099"));

    private HostEngine? _engine;
    private IReadOnlyCollection<ControllerControl> _approvalControls = [];

    private string _controllerStatus = "Searching…";
    private string _agentStatus = "Starting…";
    private string _agentState = "Unknown";
    private string _sessionId = string.Empty;
    private string _profileName = "default";
    private string _promptText = string.Empty;
    private string _pendingApprovalMessage = string.Empty;
    private bool _hasPendingApproval;
    private IBrush _controllerDotBrush = DotWarn;
    private IBrush _agentDotBrush = DotOff;
    private IBrush _profileDotBrush = DotOff;
    private bool _isControllerSearching = true;
    private bool _isAgentActive;
    private bool _isLogEmpty = true;
    private bool _isBindingsEmpty = true;
    private bool _showControllerEvents = true;
    private readonly List<LogEntry> _logHistory = [];
    private DateTimeOffset _lastViewPress = DateTimeOffset.MinValue;
    private SessionSettings _settings = new(null, null, null);
    private ChatMessage? _streamingBubble;
    private readonly TranscriptFolder _folder = new();
    private bool _isChatView = true;
    private bool _isTranscriptEmpty = true;
    private int _queuedPromptCount;
    private bool _isSetupVisible;
    private string _setupAgent = "mock";
    private string _setupWorkingDirectory = Environment.CurrentDirectory;
    private string _setupError = string.Empty;
    private string _startupError = string.Empty;
    private ControllerProfile? _profile;
    private ControllerCapabilities? _capabilities;
    private bool _isMainframePromptVisible;
    private GuiOptions _options;
    private string _workspacePath = string.Empty;

    public MainViewModel(HostEngine? engine, GuiOptions options)
    {
        _options = options;
        _agentStatus = options.Agent;
        _setupAgent = options.Agent;
        _setupWorkingDirectory = options.WorkingDirectory;
        _workspacePath = options.WorkingDirectory;

        SubmitPromptCommand = new RelayCommand(_ =>
        {
            // Clear on send. The text stayed put before, which read as "that
            // didn't go through" and made the next prompt a select-all away.
            var text = PromptText;
            PromptText = string.Empty;
            SubmitPromptText(text);
        });

        // Step from what the engine says the session is on, not from a local
        // counter: a controller binding or a Mainframe tile can move the mode
        // too, and a private index desyncs the moment one of them does.
        CyclePermissionModeCommand = new RelayCommand(_ =>
            Fire(e => e.SetPermissionModeAsync(
                AgentModes.Next(AgentModes.PermissionModes, e.Settings.PermissionMode))));
        CycleModelCommand = new RelayCommand(_ => Fire(e => e.CycleModelAsync()));
        CycleEffortCommand = new RelayCommand(_ => Fire(e => e.CycleEffortAsync()));
        CompactCommand = new RelayCommand(_ => Fire(e => e.CompactContextAsync()));
        InterruptCommand = new RelayCommand(_ => Fire(e => e.InterruptAsync()));
        NewSessionCommand = new RelayCommand(_ => Fire(e => e.NewSessionAsync()));
        ReviewCommand = new RelayCommand(_ => Fire(e => e.ReviewChangesAsync()));
        ApproveOnceCommand = new RelayCommand(_ => Fire(e => e.RespondToApprovalAsync(AgentCommandKind.ApproveOnce)));
        ApproveSessionCommand = new RelayCommand(_ => Fire(e => e.RespondToApprovalAsync(AgentCommandKind.ApproveForSession)));
        DeclineCommand = new RelayCommand(_ => Fire(e => e.RespondToApprovalAsync(AgentCommandKind.Decline)));
        CancelCommand = new RelayCommand(_ => Fire(e => e.CancelAsync()));
        PlayPatternCommand = new RelayCommand(parameter => Fire(e => PreviewPatternAsync(e, parameter as string)));
        StartSetupCommand = new RelayCommand(_ => CompleteSetup());
        PickWorkspaceCommand = new RelayCommand(_ => WorkspacePickerRequested?.Invoke());
        AttachFileCommand = new RelayCommand(_ => AttachFileRequested?.Invoke());
        StartVoiceCommand = new RelayCommand(_ => VoicePromptRequested?.Invoke());
        ClearAttachmentsCommand = new RelayCommand(_ =>
        {
            Attachments.Clear();
            RaiseAttachmentState();
        });
        DismissStartupErrorCommand = new RelayCommand(_ =>
        {
            StartupError = string.Empty;
            IsSetupVisible = true;
        });
        ConfirmMainframeCommand = new RelayCommand(_ => ConfirmMainframe());
        DismissMainframePromptCommand = new RelayCommand(_ => DismissMainframePrompt());

        if (engine is not null)
        {
            AttachEngine(engine);
        }
    }

    /// <summary>Raised when the first-run setup is submitted with valid values.</summary>
    public event Action<GuiOptions>? SetupCompleted;

    /// <summary>The options this view model was built with.</summary>
    public GuiOptions Options => _options;

    /// <summary>Raised when the user picks a different workspace to work in.</summary>
    public event Action? WorkspacePickerRequested;

    /// <summary>Raised when files should be picked (button or controller binding).</summary>
    public event Action? AttachFileRequested;

    /// <summary>Raised when dictation should start (button or controller binding).</summary>
    public event Action? VoicePromptRequested;

    /// <summary>Absolute path of the directory the agent is working in.</summary>
    public string WorkspacePath
    {
        get => _workspacePath;
        private set
        {
            if (Set(ref _workspacePath, value))
            {
                Raise(nameof(WorkspaceName));
            }
        }
    }

    /// <summary>Just the folder name — the full path is a tooltip, not a label.</summary>
    public string WorkspaceName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_workspacePath))
            {
                return "—";
            }

            var trimmed = _workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    /// <summary>
    /// Releases the current engine reference so a new one can be attached
    /// against a different workspace, and clears everything that belonged to
    /// the old session — a transcript from the previous repository is worse
    /// than no transcript.
    /// </summary>
    public void PrepareForEngineSwap(string workspacePath)
    {
        _engine = null;
        WorkspacePath = workspacePath;

        Transcript.Clear();
        IsTranscriptEmpty = true;
        _streamingBubble = null;
        _folder.Reset();
        Log.Clear();
        _logHistory.Clear();
        IsLogEmpty = true;
        HasPendingApproval = false;
        PendingApprovalMessage = string.Empty;
        SessionId = string.Empty;
        AgentState = "Starting…";
        IsAgentActive = false;
        AgentDotBrush = DotOff;
        QueuedPromptCount = 0;

        // A new engine means a new session, which starts on the agent's own
        // defaults — carrying the old workspace's model/effort/mode labels
        // over would claim settings the new session never received.
        Attachments.Clear();
        RaiseAttachmentState();
        _settings = new SessionSettings(null, null, null);
        Raise(nameof(PermissionModeLabel));
        Raise(nameof(ModelLabel));
        Raise(nameof(EffortLabel));
    }

    /// <summary>
    /// Raised by the controller fullscreen shortcuts: the Xbox/PS button, or
    /// a double-press of View.
    /// <para>
    /// The Guide press is best-effort by transport. Raw-HID DualSense reports
    /// the PS button reliably; XInput only through the undocumented ordinal-100
    /// entry point; the GameInput bridge when the device advertises the button
    /// and background guide access was requested. Steam and the Xbox Game Bar
    /// hook it globally, so a press may still be consumed before it reaches us.
    /// View double-press stays as the shortcut that works on every transport.
    /// </para>
    /// </summary>
    public event Action? MainframeRequested;

    /// <summary>
    /// Wires the running engine into this view model. Called at startup when
    /// options are already known, or after first-run setup completes.
    /// </summary>
    public void AttachEngine(HostEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (_engine is not null)
        {
            return;
        }

        _engine = engine;
        IsSetupVisible = false;
        WorkspacePath = _options.WorkingDirectory;
        ProfileName = engine.Profile.Name;
        ProfileDotBrush = DotGood;
        _profile = engine.Profile;
        RefreshBindingRows();

        _settings = engine.Settings;
        Raise(nameof(PermissionModeLabel));
        Raise(nameof(ModelLabel));
        Raise(nameof(EffortLabel));

        Buddy.SetProfile(engine.Profile);
        _approvalControls = ApprovalControls.From(engine.Profile);

        engine.LogEmitted += message => Post(() => AppendLog(message));
        engine.ControllerStatusChanged += status => Post(() =>
        {
            ControllerStatus = status;
            ControllerDotBrush = status.StartsWith("Disconnected", StringComparison.Ordinal) ? DotBad : DotWarn;
            IsControllerSearching = true;
        });
        engine.ControllerConnected += snapshot => Post(() =>
        {
            ControllerStatus =
                $"{snapshot.DisplayName}{(snapshot.Capabilities.HasFourPaddles ? " (paddles)" : string.Empty)}";
            ControllerDotBrush = DotGood;
            IsControllerSearching = false;

            // Re-coach against what this pad can actually send, and show only
            // the bindings it can reach.
            _capabilities = snapshot.Capabilities;
            Buddy.SetCapabilities(snapshot.Capabilities);
            RefreshBindingRows();
            ControllerVisual.SetPlayStationFlavor(snapshot.Id.StartsWith("dualsense", StringComparison.Ordinal));
        });
        engine.ControllerInputReceived += inputEvent => Post(() =>
        {
            ControllerVisual.Apply(inputEvent);

            // While the Mainframe prompt is up the pad answers it and nothing
            // else. Input is captured at the engine, so mapped commands are
            // already suppressed; this just routes the answer.
            if (IsMainframePromptVisible)
            {
                if (inputEvent.Kind == ControllerInputEventKind.Pressed)
                {
                    switch (inputEvent.Control)
                    {
                        case ControllerControl.A:
                            ConfirmMainframe();
                            break;
                        case ControllerControl.B:
                        case ControllerControl.Guide:
                            DismissMainframePrompt();
                            break;
                    }
                }

                return;
            }

            // Xbox/PS button = ask before entering Mainframe, on the transports
            // that report it at all (see MainframeRequested). It confirms
            // because the guide button is easy to catch with a palm and
            // Mainframe takes over the whole screen; the deliberate paths
            // (double-press View, the header button, F11) go straight in.
            if (inputEvent.Kind == ControllerInputEventKind.Pressed &&
                inputEvent.Control == ControllerControl.Guide)
            {
                ShowMainframePrompt();
            }

            // Double-press View = the same thing, on every transport.
            // Interval math uses event timestamps.
            if (inputEvent.Kind == ControllerInputEventKind.Pressed &&
                inputEvent.Control == ControllerControl.View)
            {
                if (inputEvent.Timestamp - _lastViewPress <= TimeSpan.FromMilliseconds(400))
                {
                    _lastViewPress = DateTimeOffset.MinValue;
                    MainframeRequested?.Invoke();
                }
                else
                {
                    _lastViewPress = inputEvent.Timestamp;
                }
            }
        });
        engine.AgentEventReceived += agentEvent => Post(() =>
        {
            AppendToTranscript(agentEvent);
            AgentState = agentEvent.State.ToString();
            SessionId = agentEvent.SessionId;
            AgentDotBrush = agentEvent.State switch
            {
                AgentStateKind.Working => DotBusy,
                AgentStateKind.ApprovalRequired or AgentStateKind.WaitingForInput => DotWarn,
                AgentStateKind.Error => DotBad,
                AgentStateKind.Completed or AgentStateKind.Idle => DotGood,
                _ => DotOff,
            };
            IsAgentActive = agentEvent.State is
                AgentStateKind.Working or
                AgentStateKind.ApprovalRequired or
                AgentStateKind.WaitingForInput;
            Buddy.OnAgentEvent(agentEvent);
        });
        engine.PendingApprovalChanged += message => Post(() =>
        {
            HasPendingApproval = message is not null;
            PendingApprovalMessage = message ?? string.Empty;

            // Light up the physical controls that can answer this approval.
            ControllerVisual.SetApprovalHighlight(message is null ? null : _approvalControls);
        });
        engine.VoicePromptRequested += () => Post(() => VoicePromptRequested?.Invoke());
        engine.AttachFileRequested += () => Post(() => AttachFileRequested?.Invoke());
        engine.SessionSettingsChanged += settings => Post(() =>
        {
            _settings = settings;
            Raise(nameof(PermissionModeLabel));
            Raise(nameof(ModelLabel));
            Raise(nameof(EffortLabel));
        });
        engine.PromptQueueChanged += count => Post(() =>
        {
            if (count > QueuedPromptCount)
            {
                AddChat(isUser: false, isActivity: true,
                    $"⏳ Prompt queued ({count} waiting) — sends when the current turn ends.");
            }

            QueuedPromptCount = count;
        });
        engine.ProfileApplied += applied => Post(() =>
        {
            ProfileName = applied.Name;
            _profile = applied;
            RefreshBindingRows();

            Buddy.SetProfile(applied);
            _approvalControls = ApprovalControls.From(applied);
            if (HasPendingApproval)
            {
                ControllerVisual.SetApprovalHighlight(_approvalControls);
            }
        });
    }

    internal HostEngine? Engine => _engine;

    private static Task PreviewPatternAsync(HostEngine engine, string? name)
    {
        var pattern = name switch
        {
            "working" => HapticPatternCatalog.Working,
            "approval" => HapticPatternCatalog.ApprovalRequired,
            "waiting" => HapticPatternCatalog.WaitingForInput,
            "completed" => HapticPatternCatalog.Completed,
            "error" => HapticPatternCatalog.Error,
            _ => null,
        };

        return pattern is null
            ? engine.StopHapticsAsync().AsTask()
            : engine.PlayPatternAsync(pattern).AsTask();
    }

    public ObservableCollection<LogEntry> Log { get; } = [];

    /// <summary>The Claude-app-style conversation: bubbles + activity rows.</summary>
    public ObservableCollection<ChatMessage> Transcript { get; } = [];

    public ObservableCollection<BindingRow> Bindings { get; } = [];

    public ControllerVisualViewModel ControllerVisual { get; } = new();

    public AgentBuddyViewModel Buddy { get; } = new();

    public ICommand SubmitPromptCommand { get; }

    public ICommand InterruptCommand { get; }

    public ICommand NewSessionCommand { get; }

    public ICommand ReviewCommand { get; }

    public ICommand ApproveOnceCommand { get; }

    public ICommand ApproveSessionCommand { get; }

    public ICommand DeclineCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand PlayPatternCommand { get; }

    public ICommand CyclePermissionModeCommand { get; }

    public ICommand StartSetupCommand { get; }

    /// <summary>Opens the workspace picker.</summary>
    public ICommand PickWorkspaceCommand { get; }

    /// <summary>Files chosen to hand to the agent with the next prompt.</summary>
    public ObservableCollection<string> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public string AttachmentLabel =>
        Attachments.Count == 1 ? "1 file attached" : $"{Attachments.Count} files attached";

    /// <summary>Opens the file picker.</summary>
    public ICommand AttachFileCommand { get; }

    /// <summary>Drops every attachment without sending anything.</summary>
    public ICommand ClearAttachmentsCommand { get; }

    /// <summary>Starts dictation.</summary>
    public ICommand StartVoiceCommand { get; }

    /// <summary>Adds picked files, ignoring ones already on the list.</summary>
    public void AddAttachments(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Attachments.Contains(path))
            {
                Attachments.Add(path);
            }
        }

        RaiseAttachmentState();
    }

    private void RaiseAttachmentState()
    {
        Raise(nameof(HasAttachments));
        Raise(nameof(AttachmentLabel));
    }

    public ICommand DismissStartupErrorCommand { get; }

    public ICommand ConfirmMainframeCommand { get; }

    public ICommand DismissMainframePromptCommand { get; }

    /// <summary>The "enter Mainframe?" confirmation raised by the guide button.</summary>
    public bool IsMainframePromptVisible
    {
        get => _isMainframePromptVisible;
        set => Set(ref _isMainframePromptVisible, value);
    }

    private void ShowMainframePrompt()
    {
        if (IsMainframePromptVisible)
        {
            return;
        }

        IsMainframePromptVisible = true;

        // Capture so a stray A press answers the prompt instead of firing a
        // prompt submit behind it.
        _engine?.SetInputCapture(true);
    }

    private void DismissMainframePrompt()
    {
        if (!IsMainframePromptVisible)
        {
            return;
        }

        IsMainframePromptVisible = false;
        _engine?.SetInputCapture(false);
    }

    private void ConfirmMainframe()
    {
        if (!IsMainframePromptVisible)
        {
            return;
        }

        // Release capture before handing over: Mainframe takes its own.
        IsMainframePromptVisible = false;
        _engine?.SetInputCapture(false);
        MainframeRequested?.Invoke();
    }

    public static string[] SetupAgents { get; } = ["mock", "codex", "claude"];

    /// <summary>
    /// Why the host never started. Non-empty puts a blocking explanation over
    /// the window: a failed start otherwise looks identical to a merely
    /// disconnected one, and the reason was only in the event stream.
    /// </summary>
    public string StartupError
    {
        get => _startupError;
        set
        {
            if (Set(ref _startupError, value))
            {
                Raise(nameof(HasStartupError));
            }
        }
    }

    public bool HasStartupError => !string.IsNullOrWhiteSpace(_startupError);

    public bool IsSetupVisible
    {
        get => _isSetupVisible;
        set => Set(ref _isSetupVisible, value);
    }

    public string SetupAgent
    {
        get => _setupAgent;
        set => Set(ref _setupAgent, value);
    }

    public string SetupWorkingDirectory
    {
        get => _setupWorkingDirectory;
        set => Set(ref _setupWorkingDirectory, value);
    }

    public string SetupError
    {
        get => _setupError;
        set => Set(ref _setupError, value);
    }

    public string ControllerStatus
    {
        get => _controllerStatus;
        set => Set(ref _controllerStatus, value);
    }

    public string AgentStatus
    {
        get => _agentStatus;
        set => Set(ref _agentStatus, value);
    }

    public string AgentState
    {
        get => _agentState;
        set => Set(ref _agentState, value);
    }

    public string SessionId
    {
        get => _sessionId;
        set => Set(ref _sessionId, value);
    }

    public string ProfileName
    {
        get => _profileName;
        set => Set(ref _profileName, value);
    }

    public string PromptText
    {
        get => _promptText;
        set => Set(ref _promptText, value);
    }

    public bool HasPendingApproval
    {
        get => _hasPendingApproval;
        set => Set(ref _hasPendingApproval, value);
    }

    public string PendingApprovalMessage
    {
        get => _pendingApprovalMessage;
        set => Set(ref _pendingApprovalMessage, value);
    }

    public IBrush ControllerDotBrush
    {
        get => _controllerDotBrush;
        set => Set(ref _controllerDotBrush, value);
    }

    public IBrush AgentDotBrush
    {
        get => _agentDotBrush;
        set => Set(ref _agentDotBrush, value);
    }

    public IBrush ProfileDotBrush
    {
        get => _profileDotBrush;
        set => Set(ref _profileDotBrush, value);
    }

    /// <summary>True until a controller connects; pulses the controller dot.</summary>
    public bool IsControllerSearching
    {
        get => _isControllerSearching;
        set => Set(ref _isControllerSearching, value);
    }

    /// <summary>True while the agent works or waits on an approval; pulses the agent dot.</summary>
    public bool IsAgentActive
    {
        get => _isAgentActive;
        set => Set(ref _isAgentActive, value);
    }

    public bool IsLogEmpty
    {
        get => _isLogEmpty;
        set => Set(ref _isLogEmpty, value);
    }

    public bool IsBindingsEmpty
    {
        get => _isBindingsEmpty;
        set => Set(ref _isBindingsEmpty, value);
    }

    /// <summary>Conversation view vs raw event stream in the activity card.</summary>
    public bool IsChatView
    {
        get => _isChatView;
        set => Set(ref _isChatView, value);
    }

    /// <summary>True until the first message, so the conversation can explain
    /// itself instead of showing an empty panel.</summary>
    public bool IsTranscriptEmpty
    {
        get => _isTranscriptEmpty;
        private set => Set(ref _isTranscriptEmpty, value);
    }

    /// <summary>
    /// The session knobs, mirrored from the engine so every route that can
    /// change them — chip, tile, or controller binding — moves the same label.
    /// </summary>
    public string PermissionModeLabel => $"mode: {_settings.PermissionMode ?? "default"}";

    public string ModelLabel => $"model: {_settings.Model ?? "default"}";

    public string EffortLabel => $"effort: {_settings.Effort ?? "default"}";

    public ICommand CycleModelCommand { get; }

    public ICommand CycleEffortCommand { get; }

    public ICommand CompactCommand { get; }

    public int QueuedPromptCount
    {
        get => _queuedPromptCount;
        private set
        {
            if (Set(ref _queuedPromptCount, value))
            {
                Raise(nameof(HasQueuedPrompts));
                Raise(nameof(QueuedPromptLabel));
            }
        }
    }

    public bool HasQueuedPrompts => _queuedPromptCount > 0;

    public string QueuedPromptLabel => $"queued: {_queuedPromptCount}";

    /// <summary>
    /// Sends a prompt (typed, voice, or a slash-command tile): user bubble in
    /// the transcript, then the engine — which queues it if the agent is
    /// mid-turn.
    /// </summary>
    public void SubmitPromptText(string? text)
    {
        var composed = PromptComposer.Compose(text, [.. Attachments]);
        if (Attachments.Count > 0)
        {
            Attachments.Clear();
            RaiseAttachmentState();
        }

        AddChat(isUser: true, isActivity: false,
            string.IsNullOrWhiteSpace(composed) ? "(default prompt)" : composed);
        Fire(e => e.SubmitPromptAsync(string.IsNullOrWhiteSpace(composed) ? null : composed));
    }

    /// <summary>Show or hide raw controller input lines in the event stream.</summary>
    public bool ShowControllerEvents
    {
        get => _showControllerEvents;
        set
        {
            if (Set(ref _showControllerEvents, value))
            {
                Log.Clear();
                foreach (var entry in _logHistory)
                {
                    if (value || !entry.IsControllerEvent)
                    {
                        Log.Add(entry);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Rebuilds the ACTIVE BINDINGS list for the connected device. Listing a
    /// binding the hardware cannot reach makes the panel a promise the app
    /// cannot keep; with no device known yet, everything shows.
    /// </summary>
    private void RefreshBindingRows()
    {
        Bindings.Clear();
        if (_profile is not null)
        {
            foreach (var binding in _profile.ReachableBindings(_capabilities))
            {
                Bindings.Add(BindingRow.From(binding));
            }
        }

        IsBindingsEmpty = Bindings.Count == 0;
    }

    public void AppendLog(string message)
    {
        IsLogEmpty = false;

        var entry = LogEntry.Create(message);
        _logHistory.Add(entry);
        while (_logHistory.Count > MaxLogEntries)
        {
            _logHistory.RemoveAt(0);
        }

        if (!_showControllerEvents && entry.IsControllerEvent)
        {
            return;
        }

        Log.Add(entry);
        while (Log.Count > MaxLogEntries)
        {
            Log.RemoveAt(0);
        }
    }

    /// <summary>
    /// Folds an agent event into the conversation. The rules live in
    /// <see cref="TranscriptFolder"/> so they can be tested; what stays here is
    /// the handle on the bubble being streamed into.
    /// </summary>
    private void AppendToTranscript(AgentEvent agentEvent)
    {
        switch (_folder.Fold(agentEvent))
        {
            case TranscriptAction.StartBubble start:
                _streamingBubble = AddChat(isUser: false, isActivity: false, start.Text);
                break;

            case TranscriptAction.UpdateBubble update:
                if (_streamingBubble is { } bubble)
                {
                    bubble.Text = update.Text;
                }
                else
                {
                    // The folder believes a bubble is open and this view model
                    // does not — only reachable if the two are reset out of
                    // step. Start one rather than dropping the text.
                    _streamingBubble = AddChat(isUser: false, isActivity: false, update.Text);
                }

                break;

            case TranscriptAction.AddActivity activity:
                _streamingBubble = null;
                AddChat(isUser: false, isActivity: true, activity.Text);
                break;
        }
    }

    private ChatMessage AddChat(bool isUser, bool isActivity, string text)
    {
        var message = new ChatMessage { IsUser = isUser, IsActivity = isActivity, Text = text };
        IsTranscriptEmpty = false;
        Transcript.Add(message);
        while (Transcript.Count > 200)
        {
            Transcript.RemoveAt(0);
        }

        return message;
    }

    private void Fire(Func<HostEngine, Task> action)
    {
        if (_engine is null)
        {
            return;
        }

        _ = Task.Run(() => action(_engine));
    }

    private void CompleteSetup()
    {
        var directory = SetupWorkingDirectory.Trim();
        if (!Directory.Exists(directory))
        {
            SetupError = $"Directory does not exist: {directory}";
            return;
        }

        SetupError = string.Empty;
        _options = _options with
        {
            Agent = SetupAgent.ToLowerInvariant(),
            WorkingDirectory = Path.GetFullPath(directory),
        };
        SetupCompleted?.Invoke(_options);
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);


}
