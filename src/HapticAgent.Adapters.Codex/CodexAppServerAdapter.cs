using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using HapticAgent.Core;

namespace HapticAgent.Adapters.Codex;

public sealed class CodexAppServerAdapter : IAgentAdapter
{
    private readonly AgentAdapterOptions _options;
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingResponses = new();
    private readonly ConcurrentDictionary<string, PendingServerRequest> _pendingServerRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;
    private long _nextRequestId;
    private string? _threadId;
    private string? _turnId;
    private bool _disposed;

    public CodexAppServerAdapter(AgentAdapterOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => "codex";

    public bool IsStarted { get; private set; }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsStarted)
        {
            return;
        }

        var executable = string.IsNullOrWhiteSpace(_options.ExecutablePath)
            ? "codex"
            : _options.ExecutablePath;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        if (_options.Environment is not null)
        {
            foreach (var pair in _options.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += HandleProcessExited;

        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start the Codex app-server process.");
        }

        _stdin = _process.StandardInput;
        _stdin.AutoFlush = true;
        _stdoutLoop = Task.Run(() => ReadStdoutAsync(_lifetime.Token), CancellationToken.None);
        _stderrLoop = Task.Run(() => DrainStderrAsync(_lifetime.Token), CancellationToken.None);

        try
        {
            _ = await SendRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "haptic-agent",
                        title = "HapticAgent",
                        version = "0.1.0",
                    },
                    capabilities = new
                    {
                        experimentalApi = false,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            IsStarted = true;
            Publish(AgentStateKind.Idle, "Codex app-server connected.");
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<AgentEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var agentEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return agentEvent;
        }
    }

    public async ValueTask ExecuteAsync(
        AgentCommand command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsStarted)
        {
            throw new InvalidOperationException("The Codex adapter must be started first.");
        }

        switch (command.Kind)
        {
            case AgentCommandKind.NewSession:
                await StartThreadAsync(cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.SubmitPrompt:
                await StartTurnAsync(
                    command.Text ?? "Inspect the current repository and continue with the most useful implementation task.",
                    cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.ReviewChanges:
                await StartTurnAsync(
                    "Review all current staged, unstaged, and untracked changes. Identify bugs, regressions, and missing tests.",
                    cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.Interrupt:
                await InterruptAsync(cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.ApproveOnce:
                await ResolveApprovalAsync(command.RequestId, "accept", cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.ApproveForSession:
                await ResolveApprovalAsync(command.RequestId, "acceptForSession", cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.Decline:
                await ResolveApprovalAsync(command.RequestId, "decline", cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.Cancel:
                if (!string.IsNullOrWhiteSpace(command.RequestId))
                {
                    await ResolveApprovalAsync(command.RequestId, "cancel", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await InterruptAsync(cancellationToken).ConfigureAwait(false);
                }

                break;

            case AgentCommandKind.NextSession:
            case AgentCommandKind.PreviousSession:
                Publish(AgentStateKind.Idle, "Session navigation is not implemented yet.");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unsupported Codex command.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsStarted = false;
        _lifetime.Cancel();

        foreach (var pending in _pendingResponses.Values)
        {
            pending.TrySetCanceled();
        }

        _pendingResponses.Clear();
        _pendingServerRequests.Clear();

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await AwaitLoopAsync(_stdoutLoop).ConfigureAwait(false);
        await AwaitLoopAsync(_stderrLoop).ConfigureAwait(false);

        _stdin?.Dispose();
        _process?.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
        _events.Writer.TryComplete();
    }

    private async Task StartThreadAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            "thread/start",
            new
            {
                cwd = _options.WorkingDirectory,
                approvalPolicy = "unlessTrusted",
            },
            cancellationToken).ConfigureAwait(false);

        _threadId = TryGetString(response, "thread", "id")
            ?? throw new CodexProtocolException("thread/start response did not include thread.id.");
        _turnId = null;
        Publish(AgentStateKind.Idle, "New Codex thread created.");
    }

    private async Task StartTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_threadId))
        {
            await StartThreadAsync(cancellationToken).ConfigureAwait(false);
        }

        var response = await SendRequestAsync(
            "turn/start",
            new
            {
                threadId = _threadId,
                input = new[]
                {
                    new
                    {
                        type = "text",
                        text = prompt,
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);

        _turnId = TryGetString(response, "turn", "id");
    }

    private async Task InterruptAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_threadId) || string.IsNullOrWhiteSpace(_turnId))
        {
            Publish(AgentStateKind.Idle, "There is no active Codex turn to interrupt.");
            return;
        }

        _ = await SendRequestAsync(
            "turn/interrupt",
            new
            {
                threadId = _threadId,
                turnId = _turnId,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveApprovalAsync(
        string? requestId,
        string decision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId) ||
            !_pendingServerRequests.TryRemove(requestId, out var pending))
        {
            Publish(AgentStateKind.Error, "No matching pending approval request was found.");
            return;
        }

        using var idDocument = JsonDocument.Parse(requestId);
        var payload = new Dictionary<string, object?>
        {
            ["id"] = idDocument.RootElement.Clone(),
            ["result"] = new { decision },
        };

        await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
        Publish(
            AgentStateKind.Working,
            $"Sent '{decision}' for {pending.Method}.",
            requestId,
            pending.TurnId);
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingResponses.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"Duplicate JSON-RPC request id {id}.");
        }

        try
        {
            await SendPayloadAsync(new { id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingResponses.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        SendPayloadAsync(new { method, @params = parameters }, cancellationToken);

    private async Task SendPayloadAsync(object payload, CancellationToken cancellationToken)
    {
        var writer = _stdin ?? throw new InvalidOperationException("Codex stdin is not available.");
        var line = JsonSerializer.Serialize(payload);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutAsync(CancellationToken cancellationToken)
    {
        var reader = _process?.StandardOutput
            ?? throw new InvalidOperationException("Codex stdout is not available.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                HandleIncoming(document.RootElement);
            }
            catch (JsonException exception)
            {
                Publish(AgentStateKind.Error, $"Invalid Codex JSON: {exception.Message}");
            }
        }
    }

    private async Task DrainStderrAsync(CancellationToken cancellationToken)
    {
        var reader = _process?.StandardError
            ?? throw new InvalidOperationException("Codex stderr is not available.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
        }
    }

    private void HandleIncoming(JsonElement root)
    {
        if (root.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString() ?? string.Empty;
            if (root.TryGetProperty("id", out var serverRequestId))
            {
                HandleServerRequest(method, serverRequestId, root);
            }
            else
            {
                HandleNotification(method, root);
            }

            return;
        }

        if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt64(out var id))
        {
            return;
        }

        if (!_pendingResponses.TryGetValue(id, out var completion))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            completion.TrySetException(new CodexProtocolException(error.GetRawText()));
            return;
        }

        var result = root.TryGetProperty("result", out var resultElement)
            ? resultElement.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();
        completion.TrySetResult(result);
    }

    private void HandleServerRequest(string method, JsonElement idElement, JsonElement root)
    {
        var requestId = idElement.GetRawText();
        var threadId = TryGetString(root, "params", "threadId") ?? _threadId ?? "unknown";
        var turnId = TryGetString(root, "params", "turnId") ?? _turnId;
        var message =
            TryGetString(root, "params", "reason") ??
            TryGetString(root, "params", "command") ??
            method;

        _pendingServerRequests[requestId] = new PendingServerRequest(method, threadId, turnId);

        var state = method.Equals("item/tool/requestUserInput", StringComparison.Ordinal)
            ? AgentStateKind.WaitingForInput
            : AgentStateKind.ApprovalRequired;

        Publish(state, message, requestId, turnId, threadId);
    }

    private void HandleNotification(string method, JsonElement root)
    {
        switch (method)
        {
            case "thread/started":
                _threadId = TryGetString(root, "params", "thread", "id") ?? _threadId;
                Publish(AgentStateKind.Idle, "Codex thread started.");
                break;

            case "turn/started":
                _threadId = TryGetString(root, "params", "threadId") ?? _threadId;
                _turnId = TryGetString(root, "params", "turn", "id") ?? _turnId;
                Publish(AgentStateKind.Working, "Codex is working.", turnId: _turnId);
                break;

            case "turn/completed":
                var status = TryGetString(root, "params", "turn", "status") ?? "completed";
                var error = TryGetString(root, "params", "turn", "error", "message");
                var state = status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                    ? AgentStateKind.Error
                    : status.Equals("interrupted", StringComparison.OrdinalIgnoreCase)
                        ? AgentStateKind.Idle
                        : AgentStateKind.Completed;
                Publish(state, error ?? $"Codex turn {status}.", turnId: _turnId);
                _turnId = null;
                break;

            case "serverRequest/resolved":
                var requestId = TryGetRawText(root, "params", "requestId");
                if (requestId is not null)
                {
                    _pendingServerRequests.TryRemove(requestId, out _);
                }

                break;
        }
    }

    private void HandleProcessExited(object? sender, EventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        IsStarted = false;
        var exitCode = _process?.ExitCode;
        Publish(AgentStateKind.Error, $"Codex app-server exited unexpectedly with code {exitCode?.ToString() ?? "unknown"}.");
    }

    private void Publish(
        AgentStateKind state,
        string message,
        string? requestId = null,
        string? turnId = null,
        string? sessionId = null)
    {
        _events.Writer.TryWrite(new AgentEvent(
            Id,
            sessionId ?? _threadId ?? "codex-uninitialized",
            state,
            DateTimeOffset.UtcNow,
            message,
            requestId,
            turnId));
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? TryGetRawText(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.GetRawText();
    }

    private static async Task AwaitLoopAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private sealed record PendingServerRequest(string Method, string ThreadId, string? TurnId);
}

public sealed class CodexProtocolException : Exception
{
    public CodexProtocolException(string message)
        : base(message)
    {
    }
}
