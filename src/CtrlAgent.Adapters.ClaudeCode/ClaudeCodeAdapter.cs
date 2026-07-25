using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CtrlAgent.Core;

namespace CtrlAgent.Adapters.ClaudeCode;

/// <summary>
/// Drives the Claude Code CLI over its bidirectional stream-json protocol:
/// user turns go in as JSONL, assistant/result events come back out, and
/// permission prompts arrive as can_use_tool control requests (enabled by
/// --permission-prompt-tool stdio) that map onto the approval paddles.
/// The CLI process is restarted with capped backoff if it dies.
/// </summary>
public sealed class ClaudeCodeAdapter : IAgentAdapter
{
    private const int MaxRestartAttempts = 5;

    private readonly AgentAdapterOptions _options;
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
    private readonly ConcurrentDictionary<string, PendingPermission> _pendingPermissions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;
    private long _nextControlId;
    private string? _sessionId;
    private readonly List<string> _sessionIds = [];
    private readonly object _sessionSync = new();
    private readonly System.Text.StringBuilder _streamedText = new();
    private DateTimeOffset _lastDeltaPublish = DateTimeOffset.MinValue;
    private volatile bool _replacingProcess;
    private bool _disposed;

    public ClaudeCodeAdapter(AgentAdapterOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => "claude";

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
                    Launch();
                    Publish(AgentStateKind.Idle, "Claude Code connected; waiting for the session to initialize.");
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

    public async IAsyncEnumerable<AgentEvent> ReadEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var agentEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return agentEvent;
        }
    }

    public async ValueTask ExecuteAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsStarted)
        {
            Publish(AgentStateKind.Error, $"Claude Code is not running; '{command.Kind}' was ignored.");
            return;
        }

        switch (command.Kind)
        {
            case AgentCommandKind.SubmitPrompt:
                await SendUserMessageAsync(
                    command.Text ?? "Inspect the current repository and continue with the most useful implementation task.",
                    cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.ReviewChanges:
                await SendUserMessageAsync(
                    "Review all current staged, unstaged, and untracked changes. Identify bugs, regressions, and missing tests.",
                    cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.Interrupt:
                await SendInterruptAsync(cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.ApproveOnce:
                await RespondToPermissionAsync(command.RequestId, Decision.AllowOnce, cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.ApproveForSession:
                await RespondToPermissionAsync(command.RequestId, Decision.AllowForSession, cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.Decline:
                await RespondToPermissionAsync(command.RequestId, Decision.Deny, cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.Cancel:
                if (!string.IsNullOrWhiteSpace(command.RequestId))
                {
                    await RespondToPermissionAsync(command.RequestId, Decision.Deny, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendInterruptAsync(cancellationToken).ConfigureAwait(false);
                }

                break;

            case AgentCommandKind.NewSession:
                await StartNewSessionAsync(cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.SetPermissionMode:
                await SetPermissionModeAsync(command.Text ?? "default", cancellationToken).ConfigureAwait(false);
                break;

            // Claude Code accepts its slash commands on the same stream-json
            // stdin as prompts, so these need no separate control channel.
            // Verified against CLI 2.1.220: /model and /effort report the value
            // they set, /compact says so when there is too little to compact.
            case AgentCommandKind.CompactContext:
                await SendUserMessageAsync("/compact", cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.SetModel:
                await SendUserMessageAsync($"/model {command.Text ?? "default"}", cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.SetEffort:
                await SendUserMessageAsync($"/effort {command.Text ?? "medium"}", cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.NextSession:
                await SwitchSessionAsync(+1, cancellationToken).ConfigureAwait(false);
                break;

            case AgentCommandKind.PreviousSession:
                await SwitchSessionAsync(-1, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unsupported Claude Code command.");
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
        _pendingPermissions.Clear();

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

        await AwaitQuietlyAsync(_stdoutLoop).ConfigureAwait(false);
        await AwaitQuietlyAsync(_stderrLoop).ConfigureAwait(false);

        _stdin?.Dispose();
        _process?.Dispose();
        _writeGate.Dispose();
        _startGate.Dispose();
        _lifetime.Dispose();
        _events.Writer.TryComplete();
    }

    private void Launch(string? resumeSessionId = null)
    {
        var executable = AgentExecutableResolver.Resolve(
            string.IsNullOrWhiteSpace(_options.ExecutablePath) ? "claude" : _options.ExecutablePath);

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

        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("--input-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--verbose");
        startInfo.ArgumentList.Add("--permission-prompt-tool");
        startInfo.ArgumentList.Add("stdio");
        // Claude-app-style live streaming: emit partial-message events so
        // the response can render as it is written.
        startInfo.ArgumentList.Add("--include-partial-messages");

        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add(resumeSessionId);
        }

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
            throw new InvalidOperationException("Failed to start the Claude Code process.");
        }

        var previousProcess = _process;
        var previousStdin = _stdin;
        _process = process;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = true;
        _sessionId = null;
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

        IsStarted = true;
    }

    private Task StartNewSessionAsync(CancellationToken cancellationToken) =>
        RelaunchAsync(resumeSessionId: null, "New Claude Code session starting.", cancellationToken);

    /// <summary>
    /// Cycles between the sessions this adapter has seen. The CLI runs one
    /// session per process, so switching restarts the process with
    /// <c>--resume &lt;session-id&gt;</c>; the target's history is reloaded
    /// from Claude Code's on-disk session store.
    /// </summary>
    private async Task SwitchSessionAsync(int direction, CancellationToken cancellationToken)
    {
        string? target = null;
        var position = 0;
        int count;

        lock (_sessionSync)
        {
            count = _sessionIds.Count;
            if (count > 1)
            {
                var index = _sessionId is null ? -1 : _sessionIds.IndexOf(_sessionId);
                var next = ((index < 0 ? 0 : index) + direction + count) % count;
                target = _sessionIds[next];
                position = next + 1;
            }
        }

        if (target is null)
        {
            Publish(AgentStateKind.Idle, "No other Claude Code session to switch to; use NewSession to create one.");
            return;
        }

        await RelaunchAsync(target, $"Resuming Claude Code session {position}/{count}: {target}.", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RelaunchAsync(string? resumeSessionId, string message, CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _replacingProcess = true;
            _pendingPermissions.Clear();

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

            Launch(resumeSessionId);
            Publish(AgentStateKind.Idle, message);
        }
        finally
        {
            _replacingProcess = false;
            try
            {
                _startGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // A concurrent shutdown disposed the gate.
            }
        }
    }

    private async Task SendUserMessageAsync(string text, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _interruptOutstanding, 0);
        var payload = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new[]
                {
                    new { type = "text", text },
                },
            },
        };

        await SendLineAsync(payload, cancellationToken).ConfigureAwait(false);
        Publish(AgentStateKind.Working, "Prompt sent to Claude Code.");
    }

    // 1 while an interrupt we sent has not yet been answered by a turn result.
    private int _interruptOutstanding;

    private async Task SendInterruptAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _interruptOutstanding, 1);
        var payload = ClaudeControlRequest.Interrupt($"ctrl_{Interlocked.Increment(ref _nextControlId)}");
        await SendLineAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken)
    {
        var payload = ClaudeControlRequest.SetPermissionMode(
            $"ctrl_{Interlocked.Increment(ref _nextControlId)}",
            mode);
        await SendLineAsync(payload, cancellationToken).ConfigureAwait(false);
        Publish(AgentStateKind.Idle, $"Permission mode: {mode}.");
    }

    private enum Decision
    {
        AllowOnce,
        AllowForSession,
        Deny,
    }

    private async Task RespondToPermissionAsync(string? requestId, Decision decision, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId) ||
            !_pendingPermissions.TryRemove(requestId, out var pending))
        {
            Publish(AgentStateKind.Error, "No matching pending approval request was found.");
            return;
        }

        var payload = decision switch
        {
            Decision.AllowOnce =>
                ClaudePermissionResponse.Allow(requestId, pending.ToolName, pending.Input, forSession: false),
            Decision.AllowForSession =>
                ClaudePermissionResponse.Allow(
                    requestId, pending.ToolName, pending.Input, forSession: true, pending.Suggestions),
            _ =>
                ClaudePermissionResponse.Deny(requestId, "Declined from the controller."),
        };

        await SendLineAsync(payload, cancellationToken).ConfigureAwait(false);

        var description = decision switch
        {
            Decision.AllowOnce => "allow",
            Decision.AllowForSession => $"allow {pending.ToolName} for this session",
            _ => "deny",
        };
        Publish(AgentStateKind.Working, $"Sent '{description}' for {pending.ToolName}.", requestId);
    }

    private async Task SendLineAsync(object payload, CancellationToken cancellationToken)
    {
        var writer = _stdin ?? throw new InvalidOperationException("Claude Code stdin is not available.");
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
                    HandleMessage(ClaudeStreamParser.Parse(document.RootElement));
                }
                catch (JsonException)
                {
                    // stream-json interleaves no plain text on stdout; ignore noise.
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

    private void HandleMessage(ClaudeStreamMessage message)
    {
        switch (message)
        {
            case ClaudeStreamMessage.SessionInit init:
                _sessionId = init.SessionId;
                lock (_sessionSync)
                {
                    if (!_sessionIds.Contains(init.SessionId))
                    {
                        _sessionIds.Add(init.SessionId);
                    }
                }

                var initDetails = new List<string>();
                if (init.Model is { Length: > 0 })
                {
                    initDetails.Add(init.Model);
                }

                if (init.McpSummary is { Length: > 0 })
                {
                    initDetails.Add(init.McpSummary);
                }

                if (init.SlashCommands.Count > 0)
                {
                    initDetails.Add($"{init.SlashCommands.Count} commands");
                }

                Publish(
                    AgentStateKind.Idle,
                    initDetails.Count > 0
                        ? $"Claude Code session {init.SessionId} ready ({string.Join(" · ", initDetails)})."
                        : $"Claude Code session {init.SessionId} ready.");

                if (init.SlashCommands.Count > 0)
                {
                    var listed = string.Join(" ", init.SlashCommands.Take(14));
                    var more = init.SlashCommands.Count > 14 ? " …" : string.Empty;
                    Publish(AgentStateKind.Idle, $"Commands: {listed}{more}");
                }

                break;

            case ClaudeStreamMessage.ToolResultReceived toolResult:
                _streamedText.Clear();
                Publish(
                    AgentStateKind.Working,
                    toolResult.IsError ? $"→ ⚠ {toolResult.Summary}" : $"→ {toolResult.Summary}");
                break;

            case ClaudeStreamMessage.ThinkingStarted:
                Publish(AgentStateKind.Working, "Thinking…");
                break;

            case ClaudeStreamMessage.TextDelta delta:
                // Accumulate the streamed response; publish a rolling snapshot
                // at most every 250 ms so consumers can render live text
                // without the event stream drowning.
                _streamedText.Append(delta.Text);
                var now = DateTimeOffset.UtcNow;
                if (now - _lastDeltaPublish >= TimeSpan.FromMilliseconds(250))
                {
                    _lastDeltaPublish = now;
                    Publish(AgentStateKind.Working, SnapshotStreamedText());
                }

                break;

            case ClaudeStreamMessage.AssistantActivity activity:
                // The complete message supersedes any streamed snapshot.
                _streamedText.Clear();
                Publish(AgentStateKind.Working, activity.Summary);
                break;

            case ClaudeStreamMessage.TurnResult result:
                _streamedText.Clear();

                // An interrupt we asked for comes back as a failed turn
                // ("error_during_execution"). Reporting that as an Error would
                // fire the error rumble and put CTRL·BOT in its error mood for
                // something the user did on purpose — so a turn that ends
                // while our own interrupt is outstanding completes instead.
                if (result.IsError && Interlocked.Exchange(ref _interruptOutstanding, 0) == 1)
                {
                    Publish(AgentStateKind.Completed, "Turn interrupted.");
                    break;
                }

                _interruptOutstanding = 0;
                Publish(result.IsError ? AgentStateKind.Error : AgentStateKind.Completed, result.Summary);
                break;

            case ClaudeStreamMessage.PermissionRequest permission:
                _pendingPermissions[permission.RequestId] =
                    new PendingPermission(permission.ToolName, permission.Input, permission.Suggestions);
                Publish(
                    AgentStateKind.ApprovalRequired,
                    $"Claude Code wants: {ClaudeStreamParser.DescribeToolUse(permission.ToolName, permission.Input)}",
                    permission.RequestId);
                break;

            case ClaudeStreamMessage.PermissionCanceled canceled:
                _pendingPermissions.TryRemove(canceled.RequestId, out _);
                Publish(AgentStateKind.Working, "Approval request was canceled.", canceled.RequestId);
                break;

            case ClaudeStreamMessage.ControlAck { Error: { } error }:
                Publish(AgentStateKind.Error, $"Claude Code control error: {error}");
                break;
        }
    }

    private void HandleProcessExited(object? sender, EventArgs eventArgs)
    {
        if (_disposed || _replacingProcess || sender is not Process process || !ReferenceEquals(process, _process))
        {
            return;
        }

        IsStarted = false;
        _pendingPermissions.Clear();

        int? exitCode = null;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        Publish(
            AgentStateKind.Error,
            $"Claude Code exited unexpectedly with code {exitCode?.ToString() ?? "unknown"}; attempting restart.");

        _ = Task.Run(RestartAsync);
    }

    private async Task RestartAsync()
    {
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

        // Resume the session that was live when the process died; its history
        // survives in Claude Code's on-disk session store.
        var resumeSessionId = _sessionId;

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
                    Launch(resumeSessionId);
                    Publish(
                        AgentStateKind.Idle,
                        resumeSessionId is null
                            ? $"Claude Code restarted after {attempt} attempt(s); a fresh session will initialize."
                            : $"Claude Code restarted after {attempt} attempt(s), resuming session {resumeSessionId}.");
                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Publish(AgentStateKind.Error, $"Claude Code restart attempt {attempt} failed: {exception.Message}");
                }
            }

            if (!_disposed)
            {
                Publish(
                    AgentStateKind.Error,
                    "Claude Code could not be restarted; restart CtrlAgent or switch to --agent mock.");
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

    /// <summary>Rolling view of the streamed response: the last ~500 chars.</summary>
    private string SnapshotStreamedText()
    {
        const int WindowLength = 500;
        var text = _streamedText.ToString().ReplaceLineEndings(" ").Trim();
        return text.Length <= WindowLength ? text : "…" + text[^WindowLength..];
    }

    private void Publish(AgentStateKind state, string message, string? requestId = null)
    {
        _events.Writer.TryWrite(new AgentEvent(
            Id,
            _sessionId ?? "claude-uninitialized",
            state,
            DateTimeOffset.UtcNow,
            message,
            requestId));
    }

    private static async Task AwaitQuietlyAsync(Task? task)
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

    private sealed record PendingPermission(string ToolName, JsonElement Input, JsonElement? Suggestions);
}
