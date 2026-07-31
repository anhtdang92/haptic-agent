using System.Diagnostics;
using System.Text;

namespace CtrlAgent.Gui;

internal static class StartupDiagnostics
{
    private static readonly object Sync = new();
    private static string? _explicitPath;

    public static string LogPath =>
        _explicitPath ??
        Environment.GetEnvironmentVariable("CTRLAGENT_STARTUP_LOG") ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CtrlAgent",
            "diagnostics",
            "startup.log");

    public static void Initialize(string[] args)
    {
        _explicitPath = args
            .FirstOrDefault(static argument => argument.StartsWith("--startup-log=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Record("AppDomain.UnhandledException", eventArgs.ExceptionObject as Exception,
                $"IsTerminating={eventArgs.IsTerminating}");

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Record("TaskScheduler.UnobservedTaskException", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        Record("Process.Start", null,
            $"Version={typeof(Program).Assembly.GetName().Version}; " +
            $"OS={Environment.OSVersion}; Framework={Environment.Version}; " +
            $"BaseDirectory={AppContext.BaseDirectory}; CurrentDirectory={Environment.CurrentDirectory}; " +
            $"Args={string.Join(' ', args.Select(RedactArgument))}");
    }

    public static void Record(string phase, Exception? exception = null, string? detail = null)
    {
        try
        {
            var path = LogPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append(" | pid=").Append(Environment.ProcessId)
                .Append(" | phase=").Append(phase);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                builder.Append(" | ").Append(detail);
            }

            if (exception is not null)
            {
                builder.AppendLine()
                    .Append(exception);
            }

            builder.AppendLine();
            lock (Sync)
            {
                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            }

            Trace.WriteLine(builder.ToString());
        }
        catch
        {
            // Diagnostics must never replace the original startup failure.
        }
    }

    private static string RedactArgument(string argument)
    {
        if (argument.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("secret", StringComparison.OrdinalIgnoreCase))
        {
            var separator = argument.IndexOf('=');
            return separator >= 0 ? argument[..(separator + 1)] + "<redacted>" : "<redacted>";
        }

        return argument;
    }
}
