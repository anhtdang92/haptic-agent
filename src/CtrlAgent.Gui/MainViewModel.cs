using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
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

    private readonly HostEngine? _engine;

    private string _controllerStatus = "Searching…";
    private string _agentStatus = "Starting…";
    private string _agentState = "Unknown";
    private string _sessionId = string.Empty;
    private string _profileName = "default";
    private string _promptText = string.Empty;
    private string _pendingApprovalMessage = string.Empty;
    private bool _hasPendingApproval;

    public MainViewModel(HostEngine? engine, GuiOptions options)
    {
        _engine = engine;
        _agentStatus = options.Agent;

        SubmitPromptCommand = new RelayCommand(_ => Fire(e => e.SubmitPromptAsync(PromptText)));
        InterruptCommand = new RelayCommand(_ => Fire(e => e.InterruptAsync()));
        NewSessionCommand = new RelayCommand(_ => Fire(e => e.NewSessionAsync()));
        ReviewCommand = new RelayCommand(_ => Fire(e => e.ReviewChangesAsync()));
        ApproveOnceCommand = new RelayCommand(_ => Fire(e => e.RespondToApprovalAsync(AgentCommandKind.ApproveOnce)));
        ApproveSessionCommand = new RelayCommand(_ => Fire(e => e.RespondToApprovalAsync(AgentCommandKind.ApproveForSession)));
        DeclineCommand = new RelayCommand(_ => Fire(e => e.RespondToApprovalAsync(AgentCommandKind.Decline)));
        CancelCommand = new RelayCommand(_ => Fire(e => e.CancelAsync()));
        PlayPatternCommand = new RelayCommand(parameter => Fire(e => PreviewPatternAsync(e, parameter as string)));

        if (engine is null)
        {
            return;
        }

        _profileName = engine.Profile.Name;
        foreach (var binding in engine.Profile.Bindings)
        {
            Bindings.Add(DescribeBinding(binding));
        }

        engine.LogEmitted += message => Post(() => AppendLog($"{DateTimeOffset.Now:HH:mm:ss} {message}"));
        engine.ControllerStatusChanged += status => Post(() => ControllerStatus = status);
        engine.ControllerConnected += snapshot => Post(() => ControllerStatus =
            $"{snapshot.DisplayName}{(snapshot.Capabilities.HasFourPaddles ? " (paddles)" : " (XInput fallback)")}");
        engine.AgentEventReceived += agentEvent => Post(() =>
        {
            AgentState = agentEvent.State.ToString();
            SessionId = agentEvent.SessionId;
        });
        engine.PendingApprovalChanged += message => Post(() =>
        {
            HasPendingApproval = message is not null;
            PendingApprovalMessage = message ?? string.Empty;
        });
        engine.ProfileApplied += applied => Post(() =>
        {
            ProfileName = applied.Name;
            Bindings.Clear();
            foreach (var binding in applied.Bindings)
            {
                Bindings.Add(DescribeBinding(binding));
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

    public ObservableCollection<string> Log { get; } = [];

    public ObservableCollection<string> Bindings { get; } = [];

    public ICommand SubmitPromptCommand { get; }

    public ICommand InterruptCommand { get; }

    public ICommand NewSessionCommand { get; }

    public ICommand ReviewCommand { get; }

    public ICommand ApproveOnceCommand { get; }

    public ICommand ApproveSessionCommand { get; }

    public ICommand DeclineCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand PlayPatternCommand { get; }

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

    public void AppendLog(string message)
    {
        Log.Add(message);
        while (Log.Count > MaxLogEntries)
        {
            Log.RemoveAt(0);
        }
    }

    private void Fire(Func<HostEngine, Task> action)
    {
        if (_engine is null)
        {
            return;
        }

        _ = Task.Run(() => action(_engine));
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private static string DescribeBinding(InputBinding binding)
    {
        var chord = binding.Modifiers is { Count: > 0 }
            ? string.Join("+", binding.Modifiers.OrderBy(modifier => modifier)) + "+" + binding.Control
            : binding.Control.ToString();
        var gesture = binding.Gesture == InputGesture.Press ? string.Empty : $" [{binding.Gesture}]";
        var approval = binding.RequiresPendingApproval ? " •approval" : string.Empty;
        return $"{chord}{gesture} → {binding.Command}{approval}";
    }
}
