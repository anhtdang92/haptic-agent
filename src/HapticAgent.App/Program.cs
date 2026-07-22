using HapticAgent.Adapters.Codex;
using HapticAgent.Adapters.Mock;
using HapticAgent.Core;
using HapticAgent.Platform.Windows;

namespace HapticAgent.App;

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
            Console.Error.WriteLine($"HapticAgent failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(AppOptions options)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        Console.WriteLine("HapticAgent 0.1 development host");
        Console.WriteLine($"Agent: {options.Agent}");
        Console.WriteLine($"Working directory: {options.WorkingDirectory}");
        Console.WriteLine("Press Ctrl+C or type 'quit' to exit.");
        Console.WriteLine();

        await using var provider = new WindowsControllerProvider(options.GameInputBridgeExecutable);
        await using var controller = await WaitForControllerAsync(provider, shutdown.Token).ConfigureAwait(false);
        await using var adapter = CreateAgentAdapter(options);
        await adapter.StartAsync(shutdown.Token).ConfigureAwait(false);
        await using var haptics = new HapticScheduler(controller);

        var mapping = new MappingEngine(ControllerProfile.Default);
        var state = new HostState();
        PrintControllerHelp(controller);

        var tasks = new[]
        {
            RunControllerLoopAsync(controller, adapter, mapping, state, options, shutdown.Token),
            RunAgentLoopAsync(adapter, mapping, state, haptics, shutdown.Token),
            RunConsoleLoopAsync(adapter, state, options, shutdown),
        };

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
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

    private static async Task RunControllerLoopAsync(
        IControllerDevice controller,
        IAgentAdapter adapter,
        MappingEngine mapping,
        HostState state,
        AppOptions options,
        CancellationToken cancellationToken)
    {
        await foreach (var inputEvent in controller.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (options.Verbose || inputEvent.Kind != ControllerInputEventKind.ValueChanged)
            {
                Console.WriteLine(
                    $"[controller] {inputEvent.Kind,-12} {inputEvent.Control,-24} {inputEvent.Value,6:F2}");
            }

            foreach (var command in mapping.Process(inputEvent))
            {
                var hydrated = HydrateCommand(command, state, options.DefaultPrompt);
                Console.WriteLine($"[command]    {hydrated.Kind}");
                await adapter.ExecuteAsync(hydrated, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task RunAgentLoopAsync(
        IAgentAdapter adapter,
        MappingEngine mapping,
        HostState state,
        HapticScheduler haptics,
        CancellationToken cancellationToken)
    {
        var feedback = new FeedbackRouter();

        await foreach (var agentEvent in adapter.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine($"[agent]      {agentEvent.State,-18} {agentEvent.Message ?? string.Empty}");

            if (agentEvent.State is AgentStateKind.ApprovalRequired or AgentStateKind.WaitingForInput)
            {
                state.SetPendingRequest(agentEvent.SessionId, agentEvent.RequestId);
                mapping.SetPendingApproval(agentEvent.SessionId, agentEvent.RequestId);
            }
            else if (ShouldClearPendingRequest(agentEvent))
            {
                state.ClearPendingRequest();
                mapping.SetPendingApproval(null, null);
            }

            var pattern = feedback.Route(agentEvent);
            if (pattern is not null)
            {
                await haptics.PlayAsync(pattern, cancellationToken).ConfigureAwait(false);
            }
            else if (agentEvent.State == AgentStateKind.Idle)
            {
                await haptics.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool ShouldClearPendingRequest(AgentEvent agentEvent) =>
        agentEvent.State is AgentStateKind.Completed or AgentStateKind.Error ||
        (agentEvent.State == AgentStateKind.Working && agentEvent.RequestId is not null);

    private static async Task RunConsoleLoopAsync(
        IAgentAdapter adapter,
        HostState state,
        AppOptions options,
        CancellationTokenSource shutdown)
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

            if (verb.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                verb.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                shutdown.Cancel();
                break;
            }

            if (verb.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                PrintConsoleHelp();
                continue;
            }

            var command = CreateConsoleCommand(verb, remainder, state, options.DefaultPrompt);
            if (command is null)
            {
                Console.WriteLine("Unknown command or no pending approval. Type 'help'.");
                continue;
            }

            await adapter.ExecuteAsync(command, shutdown.Token).ConfigureAwait(false);
        }
    }

    private static AgentCommand? CreateConsoleCommand(
        string verb,
        string remainder,
        HostState state,
        string defaultPrompt) =>
        verb.ToLowerInvariant() switch
        {
            "prompt" => new AgentCommand(
                AgentCommandKind.SubmitPrompt,
                Text: remainder.Length == 0 ? defaultPrompt : remainder),
            "new" => new AgentCommand(AgentCommandKind.NewSession),
            "review" => new AgentCommand(AgentCommandKind.ReviewChanges),
            "interrupt" => new AgentCommand(AgentCommandKind.Interrupt),
            "approve" => state.CreateApprovalCommand(AgentCommandKind.ApproveOnce),
            "approve-session" => state.CreateApprovalCommand(AgentCommandKind.ApproveForSession),
            "decline" => state.CreateApprovalCommand(AgentCommandKind.Decline),
            "cancel" => state.CreateApprovalCommand(AgentCommandKind.Cancel)
                ?? new AgentCommand(AgentCommandKind.Cancel),
            _ => null,
        };

    private static AgentCommand HydrateCommand(
        AgentCommand command,
        HostState state,
        string defaultPrompt)
    {
        if (command.Kind == AgentCommandKind.SubmitPrompt && string.IsNullOrWhiteSpace(command.Text))
        {
            return command with { Text = defaultPrompt };
        }

        var isApprovalCommand = command.Kind is
            AgentCommandKind.ApproveOnce or
            AgentCommandKind.ApproveForSession or
            AgentCommandKind.Decline or
            AgentCommandKind.Cancel;

        if (isApprovalCommand && string.IsNullOrWhiteSpace(command.RequestId))
        {
            return state.CreateApprovalCommand(command.Kind) ?? command;
        }

        return command;
    }

    private static void PrintControllerHelp(IControllerDevice controller)
    {
        Console.WriteLine("Controller mappings:");
        Console.WriteLine("  A             Submit default prompt");
        Console.WriteLine("  B             Interrupt active turn");
        Console.WriteLine("  X             Review changes");
        Console.WriteLine("  Menu          New session");
        Console.WriteLine("  LB + A        Run tests and fix failures");

        if (controller.Capabilities.HasFourPaddles)
        {
            Console.WriteLine("  Paddles       Approve / approve session / decline / cancel");
        }
        else
        {
            Console.WriteLine("  RB + A/Y/X/B  Approve / approve session / decline / cancel");
            Console.WriteLine("  Note: XInput cannot expose Elite paddles independently.");
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

    private sealed class HostState
    {
        private readonly object _sync = new();
        private string? _sessionId;
        private string? _requestId;

        public void SetPendingRequest(string? sessionId, string? requestId)
        {
            lock (_sync)
            {
                _sessionId = sessionId;
                _requestId = requestId;
            }
        }

        public void ClearPendingRequest()
        {
            lock (_sync)
            {
                _sessionId = null;
                _requestId = null;
            }
        }

        public AgentCommand? CreateApprovalCommand(AgentCommandKind kind)
        {
            lock (_sync)
            {
                return string.IsNullOrWhiteSpace(_requestId)
                    ? null
                    : new AgentCommand(kind, _sessionId, _requestId);
            }
        }
    }
}
