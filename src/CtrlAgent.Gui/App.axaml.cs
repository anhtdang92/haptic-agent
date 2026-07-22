using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

public sealed class App : Application
{
    private HostEngine? _engine;

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
                null);

            try
            {
                options = GuiOptions.Parse(desktop.Args ?? []);
                var profile = options.ProfilePath is null
                    ? ControllerProfile.Default
                    : ControllerProfileJson.Deserialize(File.ReadAllText(options.ProfilePath));
                _engine = new HostEngine(options, profile);
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

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += OnShutdownRequested;

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

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs eventArgs)
    {
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            // Fire-and-forget: stops rumble and kills child processes.
            _ = engine.DisposeAsync();
        }
    }
}
