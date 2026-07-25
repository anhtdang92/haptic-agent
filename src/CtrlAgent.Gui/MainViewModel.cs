using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CtrlAgent.Core;
using CtrlAgent.Hosting;

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
    private static readonly string[] PermissionModes = ["default", "plan", "acceptEdits"];
    private int _permissionModeIndex;
    private ChatMessage? _streamingBubble;
    private bool _isChatView = true;
    private int _queuedPromptCount;
    private bool _isSetupVisible;
    private string _setupAgent = "mock";
    private string _setupWorkingDirectory = Environment.CurrentDirectory;
    private string _setupError = string.Empty;
    private GuiOptions _options;

    public MainViewModel(HostEngine? engine, GuiOptions options)
    {
        _options = options;
        _agentStatus = options.Agent;
        _setupAgent = options.Agent;
        _setupWorkingDirectory = options.WorkingDirectory;

        SubmitPromptCommand = new RelayCommand(_ => SubmitPromptText(PromptText));
        CyclePermissionModeCommand = new RelayCommand(_ =>
        {
            _permissionModeIndex = (_permissionModeIndex + 1) % PermissionModes.Length;
            Raise(nameof(PermissionModeLabel));
            var mode = PermissionModes[_permissionModeIndex];
            Fire(e => e.SetPermissionModeAsync(mode));
        });
        InterruptCommand = new RelayCommand(_ => Fire(e => e.InterruptAsync()));
        NewSessionCommand = new RelayCommand(_ => Fire(e => e.NewSessionAsync()));
        ReviewCommand = new RelayCommand(_ => Fire(e => e.ReviewChangesAsync()));
        ApproveOnceCommand = new RelayCommand(_ => Fire(e => e.RespondToApprovalAsync(AgentCommandKind.ApproveOnce)));
        ApproveSessionCommand = new RelayCommand(_ => Fire(e => e.RespondToApprovalAsync(AgentCommandKind.ApproveForSession)));
        DeclineCommand = new RelayCommand(_ => Fire(e => e.RespondToApprovalAsync(AgentCommandKind.Decline)));
        CancelCommand = new RelayCommand(_ => Fire(e => e.CancelAsync()));
        PlayPatternCommand = new RelayCommand(parameter => Fire(e => PreviewPatternAsync(e, parameter as string)));
        StartSetupCommand = new RelayCommand(_ => CompleteSetup());

        if (engine is not null)
        {
            AttachEngine(engine);
        }
    }

    /// <summary>Raised when the first-run setup is submitted with valid values.</summary>
    public event Action<GuiOptions>? SetupCompleted;

    /// <summary>
    /// Raised by the controller fullscreen shortcut: double-press View.
    /// Deliberately not the Xbox/Guide button — Steam owns that for its own
    /// Big Picture, and neither XInput nor the bridge reports it anyway.
    /// </summary>
    public event Action? BigPictureRequested;

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
        ProfileName = engine.Profile.Name;
        ProfileDotBrush = DotGood;
        foreach (var binding in engine.Profile.Bindings)
        {
            Bindings.Add(BindingRow.From(binding));
        }

        IsBindingsEmpty = Bindings.Count == 0;

        Buddy.SetProfile(engine.Profile);
        _approvalControls = ComputeApprovalControls(engine.Profile);

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
            ControllerVisual.SetPlayStationFlavor(snapshot.Id.StartsWith("dualsense", StringComparison.Ordinal));
        });
        engine.ControllerInputReceived += inputEvent => Post(() =>
        {
            ControllerVisual.Apply(inputEvent);

            // Double-press View = enter Big Picture (View is unbound in the
            // default profile). Interval math uses event timestamps.
            if (inputEvent.Kind == ControllerInputEventKind.Pressed &&
                inputEvent.Control == ControllerControl.View)
            {
                if (inputEvent.Timestamp - _lastViewPress <= TimeSpan.FromMilliseconds(400))
                {
                    _lastViewPress = DateTimeOffset.MinValue;
                    BigPictureRequested?.Invoke();
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
            if (message is null && HasPendingApproval)
            {
                // Answered from anywhere — controller, GUI, overlay, or toast.
                Buddy.CountApprovalResolved();
            }

            HasPendingApproval = message is not null;
            PendingApprovalMessage = message ?? string.Empty;

            // Light up the physical controls that can answer this approval.
            ControllerVisual.SetApprovalHighlight(message is null ? null : _approvalControls);
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
            Bindings.Clear();
            foreach (var binding in applied.Bindings)
            {
                Bindings.Add(BindingRow.From(binding));
            }

            IsBindingsEmpty = Bindings.Count == 0;

            Buddy.SetProfile(applied);
            _approvalControls = ComputeApprovalControls(applied);
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

    public static string[] SetupAgents { get; } = ["mock", "codex", "claude"];

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

    public string PermissionModeLabel => $"mode: {PermissionModes[_permissionModeIndex]}";

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
    /// the transcript, bot stats, then the engine — which queues it if the
    /// agent is mid-turn.
    /// </summary>
    public void SubmitPromptText(string? text)
    {
        Buddy.CountPromptSent();
        AddChat(isUser: true, isActivity: false,
            string.IsNullOrWhiteSpace(text) ? "(default prompt)" : text);
        Fire(e => e.SubmitPromptAsync(text));
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
    /// Folds an agent event into the conversation. Streaming Working prose
    /// updates the current bubble in place; tool/plan/result lines land as
    /// activity rows and close the streaming bubble so the next prose chunk
    /// starts a fresh one — the Claude-app rhythm.
    /// </summary>
    private void AppendToTranscript(AgentEvent agentEvent)
    {
        var message = agentEvent.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        switch (agentEvent.State)
        {
            case AgentStateKind.Working when ChatMessage.IsActivityText(message):
                _streamingBubble = null;
                AddChat(isUser: false, isActivity: true, message);
                break;

            case AgentStateKind.Working:
                if (_streamingBubble is { } bubble)
                {
                    bubble.Text = message;
                }
                else
                {
                    _streamingBubble = AddChat(isUser: false, isActivity: false, message);
                }

                break;

            case AgentStateKind.Completed:
                _streamingBubble = null;
                AddChat(isUser: false, isActivity: true, $"✓ {message}");
                break;

            case AgentStateKind.Error:
                _streamingBubble = null;
                AddChat(isUser: false, isActivity: true, $"✕ {message}");
                break;

            case AgentStateKind.ApprovalRequired:
            case AgentStateKind.WaitingForInput:
                AddChat(isUser: false, isActivity: true, $"🔒 {message}");
                break;

            case AgentStateKind.Idle:
                _streamingBubble = null;
                AddChat(isUser: false, isActivity: true, message);
                break;
        }
    }

    private ChatMessage AddChat(bool isUser, bool isActivity, string text)
    {
        var message = new ChatMessage { IsUser = isUser, IsActivity = isActivity, Text = text };
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

    private static IReadOnlyCollection<ControllerControl> ComputeApprovalControls(ControllerProfile profile)
    {
        var controls = new HashSet<ControllerControl>();
        foreach (var binding in profile.Bindings)
        {
            if (!binding.RequiresPendingApproval)
            {
                continue;
            }

            controls.Add(binding.Control);
            if (binding.Modifiers is { Count: > 0 })
            {
                foreach (var modifier in binding.Modifiers)
                {
                    controls.Add(modifier);
                }
            }
        }

        return controls;
    }

}
