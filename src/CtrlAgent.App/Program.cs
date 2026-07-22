using CtrlAgent.Adapters.ClaudeCode;
using CtrlAgent.Adapters.Codex;
using CtrlAgent.Adapters.Mock;
using CtrlAgent.Core;
using CtrlAgent.Hosting;
using CtrlAgent.Platform.Windows;

namespace CtrlAgent.App;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(AppOptions.Parse(args)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CtrlAgent failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(AppOptions options)
    {
        if (options.ExportProfilePath is not null)
        {
            await File.WriteAllTextAsync(
                options.ExportProfilePath,
                ControllerProfileJson.Serialize(ControllerProfile.Default)).ConfigureAwait(false);
            Console.WriteLine($"Wrote default profile to {options.ExportProfilePath}");
            return 0;
        }

        var profile = ControllerProfile.Default;
        if (options.ProfilePath is not null)
        {
            var json = await File.ReadAllTextAsync(options.ProfilePath).ConfigureAwait(false);
            profile = ControllerProfileJson.Deserialize(json);
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        if (options.Validate)
        {
            await using var validationProvider = new WindowsControllerProvider(options.GameInputBridgeExecutable);
            await using var validationController =
                await WaitForControllerAsync(validationProvider, shutdown.Token).ConfigureAwait(false);
            return await ValidationWizard.RunAsync(
                validationController,
                options.WorkingDirectory,
                shutdown.Token).ConfigureAwait(false);
        }

        Console.WriteLine("CtrlAgent 0.1 development host");
        Console.WriteLine($"Agent: {options.Agent}");
        Console.WriteLine($"Working directory: {options.WorkingDirectory}");
        Console.WriteLine($"Profile: {profile.Name} ({profile.Bindings.Count} bindings)");
        Console.WriteLine("Press Ctrl+C or type 'quit' to exit.");
        Console.WriteLine();
        Console.WriteLine("Looking for an Xbox controller...");

        var engine = new HostEngine(
            new WindowsControllerProvider(options.GameInputBridgeExecutable),
            CreateAgentAdapter(options),
            profile,
            new HostEngineOptions(options.DefaultPrompt, options.Verbose));

        engine.LogEmitted += Console.WriteLine;
        engine.ControllerConnected += snapshot => PrintControllerHelp(snapshot, profile);

        try
        {
            await engine.StartAsync(shutdown.Token).ConfigureAwait(false);
            await RunConsoleLoopAsync(engine, shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static IAgentAdapter CreateAgentAdapter(AppOptions options) =>
        options.Agent switch
        {
            "mock" => new MockAgentAdapter(),
            "codex" => new CodexAppServerAdapter(new AgentAdapterOptions(
                options.WorkingDirectory,
                options.CodexExecutable)),
            "claude" => new ClaudeCodeAdapter(new AgentAdapterOptions(
                options.WorkingDirectory,
                options.ClaudeExecutable)),
            _ => throw new InvalidOperationException($"Unsupported agent adapter: {options.Agent}"),
        };

    private static async Task<IControllerDevice> WaitForControllerAsync(
        IControllerProvider provider,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("Looking for an Xbox controller...");

        while (!cancellationToken.IsCancellationRequested)
        {
            var controller = await provider.GetPrimaryControllerAsync(cancellationToken).ConfigureAwait(false);
            if (controller is { IsConnected: true })
            {
                Console.WriteLine($"Connected: {controller.DisplayName}");
                Console.WriteLine();
                return controller;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task RunConsoleLoopAsync(HostEngine engine, CancellationTokenSource shutdown)
    {
        PrintConsoleHelp();

        while (!shutdown.IsCancellationRequested)
        {
            Console.Write("> ");
            var line = await Console.In.ReadLineAsync(shutdown.Token).ConfigureAwait(false);
            if (line is null)
            {
                shutdown.Cancel();
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var split = trimmed.IndexOf(' ');
            var verb = split < 0 ? trimmed : trimmed[..split];
            var remainder = split < 0 ? string.Empty : trimmed[(split + 1)..].Trim();

            switch (verb.ToLowerInvariant())
            {
                case "quit":
                case "exit":
                    shutdown.Cancel();
                    return;

                case "help":
                    PrintConsoleHelp();
                    break;

                case "prompt":
                    await engine.SubmitPromptAsync(remainder.Length == 0 ? null : remainder).ConfigureAwait(false);
                    break;

                case "new":
                    await engine.NewSessionAsync().ConfigureAwait(false);
                    break;

                case "review":
                    await engine.ReviewChangesAsync().ConfigureAwait(false);
                    break;

                case "interrupt":
                    await engine.InterruptAsync().ConfigureAwait(false);
                    break;

                case "approve":
                    await engine.RespondToApprovalAsync(AgentCommandKind.ApproveOnce).ConfigureAwait(false);
                    break;

                case "approve-session":
                    await engine.RespondToApprovalAsync(AgentCommandKind.ApproveForSession).ConfigureAwait(false);
                    break;

                case "decline":
                    await engine.RespondToApprovalAsync(AgentCommandKind.Decline).ConfigureAwait(false);
                    break;

                case "cancel":
                    await engine.CancelAsync().ConfigureAwait(false);
                    break;

                default:
                    Console.WriteLine("Unknown command. Type 'help'.");
                    break;
            }
        }
    }

    private static void PrintControllerHelp(ControllerSnapshot controller, ControllerProfile profile)
    {
        Console.WriteLine($"Controller mappings ({profile.Name}):");

        foreach (var binding in profile.Bindings)
        {
            var chord = binding.Modifiers is { Count: > 0 }
                ? string.Join("+", binding.Modifiers.OrderBy(modifier => modifier)) + "+" + binding.Control
                : binding.Control.ToString();
            var gesture = binding.Gesture == InputGesture.Press
                ? string.Empty
                : $" [{binding.Gesture}]";
            var approval = binding.RequiresPendingApproval
                ? " (needs pending approval)"
                : string.Empty;

            Console.WriteLine($"  {chord,-32}{binding.Command}{gesture}{approval}");
        }

        if (!controller.Capabilities.HasFourPaddles)
        {
            Console.WriteLine("  Note: XInput cannot expose Elite paddles independently; paddle bindings are inactive.");
        }

        Console.WriteLine();
    }

    private static void PrintConsoleHelp()
    {
        Console.WriteLine("Console commands:");
        Console.WriteLine("  prompt [text], new, review, interrupt");
        Console.WriteLine("  approve, approve-session, decline, cancel");
        Console.WriteLine("  help, quit");
        Console.WriteLine();
    }
}
