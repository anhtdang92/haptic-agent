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

            case AgentCommandKind.NextSession:
            case AgentCommandKind.PreviousSession:
                Publish(
                    AgentStateKind.Idle,
                    "Claude Code runs one session per process; use NewSession for a fresh one.");
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

    private void Launch()
    {
        var executable = string.IsNullOrWhiteSpace(_options.ExecutablePath)
            ? "claude"
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

        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("--input-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--verbose");
        startInfo.ArgumentList.Add("--permission-prompt-tool");
        startInfo.ArgumentList.Add("stdio");

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

    private async Task StartNewSessionAsync(CancellationToken cancellationToken)
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

            Launch();
            Publish(AgentStateKind.Idle, "New Claude Code session starting.");
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

    private async Task SendInterruptAsync(CancellationToken cancellationToken)
    {
        var payload = new
        {
            type = "control_request",
            request_id = $"ctrl_{Interlocked.Increment(ref _nextControlId)}",
            request = new { subtype = "interrupt" },
        };

        await SendLineAsync(payload, cancellationToken).ConfigureAwait(false);
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
                ClaudePermissionResponse.Allow(requestId, pending.ToolName, pending.Input, forSession: true),
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
                Publish(AgentStateKind.Idle, $"Claude Code session {init.SessionId} ready.");
                break;

            case ClaudeStreamMessage.AssistantActivity activity:
                Publish(AgentStateKind.Working, activity.Summary);
                break;

            case ClaudeStreamMessage.TurnResult result:
                Publish(result.IsError ? AgentStateKind.Error : AgentStateKind.Completed, result.Summary);
                break;

            case ClaudeStreamMessage.PermissionRequest permission:
                _pendingPermissions[permission.RequestId] = new PendingPermission(permission.ToolName, permission.Input);
                Publish(
                    AgentStateKind.ApprovalRequired,
                    $"Claude Code wants to use {permission.ToolName}.",
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
                    Launch();
                    Publish(
                        AgentStateKind.Idle,
                        $"Claude Code restarted after {attempt} attempt(s); a fresh session will initialize.");
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

    private sealed record PendingPermission(string ToolName, JsonElement Input);
}
