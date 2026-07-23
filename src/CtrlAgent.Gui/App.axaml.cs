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
    private TrayIcon? _trayIcon;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startupError = default(string);
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
                if (args.Length == 0 && GuiSettings.TryLoad() is { } saved)
                {
                    // No CLI arguments: pick up where the user left off.
                    options = saved.ApplyTo(options);
                }

                var profile = options.ProfilePath is null
                    ? ControllerProfile.Default
                    : ControllerProfileJson.Deserialize(File.ReadAllText(options.ProfilePath));

                _engine = new HostEngine(
                    new WindowsControllerProvider(options.GameInputBridgeExecutable),
                    CreateAgentAdapter(options),
                    profile,
                    new HostEngineOptions(options.DefaultPrompt));

                GuiSettings.TrySave(options);
            }
            catch (Exception exception)
            {
                startupError = exception.Message;
                _engine = null;
            }

            var viewModel = new MainViewModel(_engine, options);
            if (startupError is not null)
            {
                viewModel.AppendLog($"Startup failed: {startupError}");
                viewModel.ControllerStatus = "Unavailable";
                viewModel.AgentStatus = "Unavailable";
            }

            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CtrlAgent.Gui/Assets/icon.png")));
            var mainWindow = new MainWindow { DataContext = viewModel, Icon = icon };

            // Closing the window hides to tray; only the tray Exit (or an OS
            // shutdown) actually quits.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            mainWindow.Closing += (_, eventArgs) =>
            {
                if (!_exiting)
                {
                    eventArgs.Cancel = true;
                    mainWindow.Hide();
                }
            };

            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += OnShutdownRequested;

            SetUpTrayIcon(desktop, mainWindow, icon);
            mainWindow.Show();

            if (_engine is not null)
            {
                var engine = _engine;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await engine.StartAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            viewModel.AppendLog($"Host failed to start: {exception.Message}"));
                    }
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetUpTrayIcon(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow mainWindow,
        WindowIcon icon)
    {
        var showItem = new NativeMenuItem("Show CtrlAgent");
        showItem.Click += (_, _) => ShowMainWindow(mainWindow);

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => Exit(desktop);

        var menu = new NativeMenu();
        menu.Items.Add(showItem);
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

    private void Exit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _exiting = true;
        desktop.Shutdown();
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
