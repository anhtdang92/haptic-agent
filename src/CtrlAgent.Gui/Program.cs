using Avalonia;

namespace CtrlAgent.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupDiagnostics.Initialize(args);
        try
        {
            StartupDiagnostics.Record("Avalonia.Starting");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            StartupDiagnostics.Record("Avalonia.ExitedNormally");
            return 0;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Record("Avalonia.StartupFailed", exception);
            return exception.HResult != 0 ? exception.HResult : 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
