using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using CtrlAgent.Adapters.ClaudeCode;
using CtrlAgent.Adapters.Codex;
using CtrlAgent.Adapters.Mock;
using CtrlAgent.Core;
using CtrlAgent.Hosting;
using CtrlAgent.Platform.Windows;

namespace CtrlAgent.Gui;

public sealed class App : Application
{
    private HostEngine? _engine;
    private GuiOptions? _activeOptions;
    private TrayIcon? _trayIcon;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private OverlayWindow? _overlay;
    private MainframeWindow? _mainframe;
    private ToastWindow? _toast;
    private bool _toastIsApproval;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startupError = default(string);
            var firstRun = false;
            GuiSettings? savedSettings = null;
            var options = new GuiOptions(
                "mock",
                Environment.CurrentDirectory,
                "Inspect the current repository and continue implementing the highest-priority unfinished task.",
                null,
                null,
                null,
                null);

            try
            {
                var args = desktop.Args ?? [];
                options = GuiOptions.Parse(args);
                if (args.Length == 0)
                {
                    if (GuiSettings.TryLoad() is { } saved)
                    {
                        // No CLI arguments: pick up where the user left off.
                        savedSettings = saved;
                        options = saved.ApplyTo(options);
                    }
                    else
                    {
                        firstRun = true;
                    }
                }
            }
            catch (Exception exception)
            {
                startupError = exception.Message;
            }

            var viewModel = new MainViewModel(null, options);
            if (startupError is not null)
            {
                viewModel.AppendLog($"Startup failed: {startupError}");
                viewModel.StartupError = startupError;
                viewModel.ControllerStatus = "Failed to start";
                viewModel.AgentStatus = "Failed to start";
            }

            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CtrlAgent.Gui/Assets/icon.png")));
            var mainWindow = new MainWindow { DataContext = viewModel, Icon = icon };
            _viewModel = viewModel;
            _mainWindow = mainWindow;

            // Restore remembered view toggles and window placement.
            if (savedSettings is not null)
            {
                viewModel.IsChatView = savedSettings.ChatView ?? true;
                viewModel.ShowControllerEvents = savedSettings.ShowControllerInput ?? true;
                if (savedSettings is { WindowWidth: >= 600 and <= 8192, WindowHeight: >= 400 and <= 8192 })
                {
                    mainWindow.Width = savedSettings.WindowWidth.Value;
                    mainWindow.Height = savedSettings.WindowHeight.Value;
                }

                if (savedSettings is { WindowX: { } x and > -4000, WindowY: { } y and > -4000 })
                {
                    mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    mainWindow.Position = new PixelPoint(x, y);
                }
            }

            // Closing the window hides to tray; only the tray Exit (or an OS
            // shutdown) actually quits.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            mainWindow.Closing += (_, eventArgs) =>
            {
                if (!_exiting)
                {
                    eventArgs.Cancel = true;
                    SaveUiState();
                    mainWindow.Hide();
                }
            };

            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += OnShutdownRequested;

            SetUpTrayIcon(desktop, mainWindow, icon);
            mainWindow.Show();

            viewModel.SetupCompleted += StartWithOptions;
            viewModel.MainframeRequested += ShowMainframe;
            if (startupError is null)
            {
                if (firstRun)
                {
                    viewModel.IsSetupVisible = true;
                }
                else
                {
                    StartWithOptions(options);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Creates and starts the engine for the given options (initial
    /// launch or first-run setup submission).</summary>
    private void StartWithOptions(GuiOptions options)
    {
        if (_viewModel is null || _engine is not null)
        {
            return;
        }

        try
        {
            var profile = options.ProfilePath is null
                ? ControllerProfile.Default
                : ControllerProfileJson.Deserialize(File.ReadAllText(options.ProfilePath));

            var engine = new HostEngine(
                new WindowsControllerProvider(options.GameInputBridgeExecutable),
                CreateAgentAdapter(options),
                profile,
                new HostEngineOptions(options.DefaultPrompt));

            _engine = engine;
            _activeOptions = options;
            _viewModel.AttachEngine(engine);
            _viewModel.AgentStatus = options.Agent;
            GuiSettings.TrySave(options);

            engine.AgentEventReceived += agentEvent =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => MaybeToast(agentEvent));
            engine.PendingApprovalChanged += message =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // The approval got answered elsewhere; retire its toast.
                    if (message is null && _toastIsApproval)
                    {
                        _toast?.Close();
                    }
                });

            var viewModel = _viewModel;
            _ = Task.Run(async () =>
            {
                try
                {
                    await engine.StartAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        viewModel.AppendLog($"Host failed to start: {exception.Message}");
                        viewModel.StartupError = exception.Message;
                    });
                }
            });
        }
        catch (Exception exception)
        {
            // Bounce back to setup, but say why — an unexplained dialog reads
            // as the app losing the settings the user already entered.
            _viewModel.AppendLog($"Startup failed: {exception.Message}");
            _viewModel.SetupError = exception.Message;
            _viewModel.IsSetupVisible = true;
        }
    }

    private void SetUpTrayIcon(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow mainWindow,
        WindowIcon icon)
    {
        var showItem = new NativeMenuItem("Show CtrlAgent");
        showItem.Click += (_, _) => ShowMainWindow(mainWindow);

        var overlayItem = new NativeMenuItem("Toggle overlay");
        overlayItem.Click += (_, _) => ToggleOverlay();

        var mainframeItem = new NativeMenuItem("Mainframe mode");
        mainframeItem.Click += (_, _) => ShowMainframe();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => Exit(desktop);

        var menu = new NativeMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(overlayItem);
        menu.Items.Add(mainframeItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "CtrlAgent",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow(mainWindow);

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private static void ShowMainWindow(MainWindow mainWindow)
    {
        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    /// <summary>
    /// Shows a notification card when nothing else is on screen: main window
    /// hidden/minimized and no overlay visible.
    /// </summary>
    private void MaybeToast(CtrlAgent.Core.AgentEvent agentEvent)
    {
        if (_exiting || _viewModel is null)
        {
            return;
        }

        var mainVisible = _mainWindow is { IsVisible: true } && _mainWindow.WindowState != WindowState.Minimized;
        var overlayVisible = _overlay is { IsVisible: true };
        var bigPictureVisible = _mainframe is { IsVisible: true };
        if (mainVisible || overlayVisible || bigPictureVisible)
        {
            return;
        }

        switch (agentEvent.State)
        {
            case CtrlAgent.Core.AgentStateKind.ApprovalRequired:
            case CtrlAgent.Core.AgentStateKind.WaitingForInput:
                ShowToast("APPROVAL REQUIRED", agentEvent.Message ?? "The agent needs permission.", "#FFB020", approval: true);
                break;
            case CtrlAgent.Core.AgentStateKind.Completed:
                ShowToast("TURN COMPLETED", agentEvent.Message ?? "The agent finished.", "#34F5A4", approval: false);
                break;
            case CtrlAgent.Core.AgentStateKind.Error:
                ShowToast("AGENT ERROR", agentEvent.Message ?? "Something went wrong.", "#FF5A78", approval: false);
                break;
        }
    }

    private void ShowToast(string title, string message, string accentHex, bool approval)
    {
        _toast?.Close();

        var toast = new ToastWindow();
        toast.Configure(title, message, accentHex, approval);

        if (approval && _viewModel is { } viewModel)
        {
            toast.ApproveRequested += () => viewModel.ApproveOnceCommand.Execute(null);
            toast.DeclineRequested += () => viewModel.DeclineCommand.Execute(null);
        }

        toast.OpenRequested += () =>
        {
            if (_mainWindow is not null)
            {
                ShowMainWindow(_mainWindow);
            }
        };
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_toast, toast))
            {
                _toast = null;
                _toastIsApproval = false;
            }
        };

        _toast = toast;
        _toastIsApproval = approval;
        toast.Show();
    }

    /// <summary>
    /// Opens (or focuses) the fullscreen controller-first Mainframe mode.
    /// While it is open the engine captures controller input for UI
    /// navigation; approval bindings keep working. Closing releases capture.
    /// </summary>
    public void ShowMainframe()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.Engine is null)
        {
            _viewModel.AppendLog("Finish the first-run setup before entering Mainframe mode.");
            return;
        }

        if (_mainframe is not null)
        {
            _mainframe.Activate();
            return;
        }

        var viewModel = new MainframeViewModel(_viewModel);
        var window = new MainframeWindow { DataContext = viewModel };
        viewModel.CloseRequested += window.Close;
        window.Closed += (_, _) =>
        {
            viewModel.Detach();
            if (ReferenceEquals(_mainframe, window))
            {
                _mainframe = null;
            }
        };

        _mainframe = window;
        window.Show();
        window.Activate();
    }

    /// <summary>Shows or hides the always-on-top HUD strip.</summary>
    public void ToggleOverlay()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_overlay is null)
        {
            _overlay = new OverlayWindow { DataContext = _viewModel };
            _overlay.OpenMainRequested += () =>
            {
                if (_mainWindow is not null)
                {
                    ShowMainWindow(_mainWindow);
                }
            };

            // Default to the top-right corner of the primary work area.
            if (_mainWindow?.Screens.Primary?.WorkingArea is { } area)
            {
                _overlay.Position = new PixelPoint(area.Right - 420, area.Y + 24);
            }

            _overlay.Show();
            return;
        }

        if (_overlay.IsVisible)
        {
            _overlay.Hide();
        }
        else
        {
            _overlay.Show();
        }
    }

    private void Exit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _exiting = true;
        desktop.Shutdown();
    }

    /// <summary>Persists view toggles and window placement (best-effort).</summary>
    private void SaveUiState()
    {
        if (_activeOptions is null || _viewModel is null || _mainWindow is null)
        {
            return;
        }

        GuiSettings.TrySave(_activeOptions, new UiState(
            _viewModel.IsChatView,
            _viewModel.ShowControllerEvents,
            _mainWindow.ClientSize.Width,
            _mainWindow.ClientSize.Height,
            _mainWindow.Position.X,
            _mainWindow.Position.Y));
    }

    private static IAgentAdapter CreateAgentAdapter(GuiOptions options) =>
        options.Agent switch
        {
            "codex" => new CodexAppServerAdapter(new AgentAdapterOptions(
                options.WorkingDirectory,
                options.CodexExecutable)),
            "claude" => new ClaudeCodeAdapter(new AgentAdapterOptions(
                options.WorkingDirectory,
                options.ClaudeExecutable)),
            _ => new MockAgentAdapter(),
        };

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs eventArgs)
    {
        _exiting = true;
        SaveUiState();
        _toast?.Close();
        _toast = null;

        _mainframe?.Close();
        _mainframe = null;

        if (_overlay is not null)
        {
            _overlay.AllowClose = true;
            _overlay.Close();
            _overlay = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;

        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            // Fire-and-forget: stops rumble and kills child processes.
            _ = engine.DisposeAsync();
        }
    }
}
