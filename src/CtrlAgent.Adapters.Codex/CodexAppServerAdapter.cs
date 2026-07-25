using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CtrlAgent.Core;

namespace CtrlAgent.Adapters.Codex;

public sealed class CodexAppServerAdapter : IAgentAdapter
{
    private readonly AgentAdapterOptions _options;
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingResponses = new();
    private readonly ConcurrentDictionary<string, PendingServerRequest> _pendingServerRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private const int MaxRestartAttempts = 5;

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;
    private long _nextRequestId;
    private readonly List<string> _threadIds = [];
    private readonly object _threadSync = new();
    private string? _threadId;
    private string? _turnId;
    private string? _resumeThreadId;
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

        try
        {
            await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsStarted)
                {
                    await LaunchAsync(cancellationToken).ConfigureAwait(false);
                    Publish(AgentStateKind.Idle, "Codex app-server connected.");
                }
            }
            finally
            {
                _startGate.Release();
            }
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        var executable = AgentExecutableResolver.Resolve(
            string.IsNullOrWhiteSpace(_options.ExecutablePath) ? "codex" : _options.ExecutablePath);

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

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += HandleProcessExited;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the Codex app-server process.");
        }

        var previousProcess = _process;
        var previousStdin = _stdin;
        _process = process;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = true;
        _stdoutLoop = Task.Run(() => ReadStdoutAsync(process.StandardOutput, _lifetime.Token), CancellationToken.None);
        _stderrLoop = Task.Run(() => DrainStderrAsync(process.StandardError, _lifetime.Token), CancellationToken.None);

        try
        {
            previousStdin?.Dispose();
            previousProcess?.Dispose();
        }
        catch (InvalidOperationException)
        {
        }

        _ = await SendRequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "haptic-agent",
                    title = "CtrlAgent",
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
            // Never crash the host input loop while the app-server restarts.
            Publish(AgentStateKind.Error, $"Codex app-server is not running; '{command.Kind}' was ignored.");
            return;
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

            case AgentCommandKind.SetPermissionMode:
                Publish(AgentStateKind.Idle, "Codex permission modes are not wired yet; approval policy stays unlessTrusted.");
                break;

            case AgentCommandKind.CompactContext:
            case AgentCommandKind.SetModel:
            case AgentCommandKind.SetEffort:
                Publish(AgentStateKind.Idle, $"Codex does not expose {command.Kind} yet.");
                break;

            case AgentCommandKind.NextSession:
                await SwitchThreadAsync(+1, cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.PreviousSession:
                await SwitchThreadAsync(-1, cancellationToken).ConfigureAwait(false);
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
        _startGate.Dispose();
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

        var threadId = TryGetString(response, "thread", "id")
            ?? throw new CodexProtocolException("thread/start response did not include thread.id.");
        RememberThread(threadId);
        _threadId = threadId;
        _turnId = null;
        Publish(AgentStateKind.Idle, "New Codex thread created.");
    }

    private void RememberThread(string threadId)
    {
        lock (_threadSync)
        {
            if (!_threadIds.Contains(threadId))
            {
                _threadIds.Add(threadId);
            }
        }
    }

    private async Task SwitchThreadAsync(int direction, CancellationToken cancellationToken)
    {
        string? target = null;
        int position = 0;
        int count;

        lock (_threadSync)
        {
            count = _threadIds.Count;
            if (count > 1)
            {
                var index = _threadId is null ? -1 : _threadIds.IndexOf(_threadId);
                var next = ((index < 0 ? 0 : index) + direction + count) % count;
                target = _threadIds[next];
                position = next + 1;
            }
        }

        if (target is null)
        {
            Publish(AgentStateKind.Idle, "No other Codex thread to switch to; use NewSession to create one.");
            return;
        }

        // Threads remembered from before a crash only exist on disk until they
        // are resumed; resuming an already-live thread is harmless, so always
        // ask the server to load the target.
        try
        {
            _ = await SendRequestAsync(
                "thread/resume",
                new
                {
                    threadId = target,
                    cwd = _options.WorkingDirectory,
                    approvalPolicy = "unlessTrusted",
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CodexProtocolException exception)
        {
            Publish(AgentStateKind.Error, $"Could not switch to thread {target}: {exception.Message}");
            return;
        }

        _threadId = target;
        _turnId = null;
        Publish(AgentStateKind.Idle, $"Active thread {position}/{count}: {target}.");
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

    private async Task ReadStdoutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
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
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The process died; HandleProcessExited owns recovery.
        }
    }

    private async Task DrainStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Applies one parsed message. Classification lives in
    /// <see cref="CodexProtocolParser"/>; what stays here is the part that
    /// genuinely needs adapter state — which thread and turn are current, and
    /// which requests are outstanding.
    /// </summary>
    private void HandleIncoming(JsonElement root)
    {
        switch (CodexProtocolParser.Parse(root))
        {
            case CodexMessage.ThreadStarted started:
                if (started.ThreadId is { Length: > 0 } startedThreadId)
                {
                    RememberThread(startedThreadId);
                    _threadId = startedThreadId;
                }

                Publish(AgentStateKind.Idle, "Codex thread started.");
                break;

            case CodexMessage.TurnStarted turn:
                // The wire may omit either id on a continuation; keep what we
                // already knew rather than dropping to null mid-turn.
                _threadId = turn.ThreadId ?? _threadId;
                _turnId = turn.TurnId ?? _turnId;
                Publish(AgentStateKind.Working, "Codex is working.", turnId: _turnId);
                break;

            case CodexMessage.TurnFinished finished:
                Publish(finished.State, finished.Summary, turnId: _turnId);
                _turnId = null;
                break;

            case CodexMessage.UserActionRequired request:
                var threadId = request.ThreadId ?? _threadId ?? "unknown";
                var requestTurnId = request.TurnId ?? _turnId;
                _pendingServerRequests[request.RequestId] =
                    new PendingServerRequest(request.Method, threadId, requestTurnId);
                Publish(request.State, request.Message, request.RequestId, requestTurnId, threadId);
                break;

            case CodexMessage.ServerRequestResolved resolved:
                _pendingServerRequests.TryRemove(resolved.RequestId, out _);
                break;

            case CodexMessage.ResponseReceived response:
                if (!_pendingResponses.TryGetValue(response.Id, out var completion))
                {
                    break;
                }

                if (response.Error is { } error)
                {
                    completion.TrySetException(new CodexProtocolException(error));
                }
                else
                {
                    completion.TrySetResult(response.Result);
                }

                break;
        }
    }

    private void HandleProcessExited(object? sender, EventArgs eventArgs)
    {
        if (_disposed || sender is not Process process || !ReferenceEquals(process, _process))
        {
            return;
        }

        IsStarted = false;
        // Codex persists threads on disk, so the restarted server can resume
        // the one that was active when the process died.
        _resumeThreadId = _threadId;
        _threadId = null;
        _turnId = null;

        int? exitCode = null;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        FailAllPending($"Codex app-server exited with code {exitCode?.ToString() ?? "unknown"}.");
        Publish(
            AgentStateKind.Error,
            $"Codex app-server exited unexpectedly with code {exitCode?.ToString() ?? "unknown"}; attempting restart.");

        _ = Task.Run(RestartAsync);
    }

    private async Task RestartAsync()
    {
        // Only one restart loop at a time; a concurrent StartAsync also holds
        // this gate.
        if (!await _startGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        CancellationToken lifetime;
        try
        {
            lifetime = _lifetime.Token;
        }
        catch (ObjectDisposedException)
        {
            _startGate.Release();
            return;
        }

        try
        {
            for (var attempt = 1; attempt <= MaxRestartAttempts && !_disposed; attempt++)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(15, 1 << attempt));
                try
                {
                    await Task.Delay(delay, lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await LaunchAsync(lifetime).ConfigureAwait(false);
                    await ResumeThreadAfterRestartAsync(attempt, lifetime).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Publish(AgentStateKind.Error, $"Codex restart attempt {attempt} failed: {exception.Message}");
                }
            }

            if (!_disposed)
            {
                Publish(
                    AgentStateKind.Error,
                    "Codex app-server could not be restarted; restart CtrlAgent or switch to --agent mock.");
            }
        }
        finally
        {
            try
            {
                _startGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// After a crash restart, picks the interrupted conversation back up by
    /// resuming the previously active thread from Codex's on-disk rollout.
    /// Falls back to a fresh thread (on the next prompt) when resume fails.
    /// </summary>
    private async Task ResumeThreadAfterRestartAsync(int attempt, CancellationToken cancellationToken)
    {
        var resumeId = _resumeThreadId;
        if (string.IsNullOrWhiteSpace(resumeId))
        {
            Publish(
                AgentStateKind.Idle,
                $"Codex app-server restarted after {attempt} attempt(s); the next prompt starts a fresh thread.");
            return;
        }

        try
        {
            var response = await SendRequestAsync(
                "thread/resume",
                new
                {
                    threadId = resumeId,
                    cwd = _options.WorkingDirectory,
                    approvalPolicy = "unlessTrusted",
                },
                cancellationToken).ConfigureAwait(false);

            var threadId = TryGetString(response, "thread", "id") ?? resumeId;
            RememberThread(threadId);
            _threadId = threadId;
            _resumeThreadId = null;
            Publish(
                AgentStateKind.Idle,
                $"Codex app-server restarted after {attempt} attempt(s) and resumed thread {threadId}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _resumeThreadId = null;
            Publish(
                AgentStateKind.Idle,
                $"Codex app-server restarted after {attempt} attempt(s); could not resume the previous thread " +
                $"({exception.Message}). The next prompt starts a fresh one.");
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var id in _pendingResponses.Keys.ToArray())
        {
            if (_pendingResponses.TryRemove(id, out var pending))
            {
                pending.TrySetException(new CodexProtocolException(reason));
            }
        }

        _pendingServerRequests.Clear();
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
