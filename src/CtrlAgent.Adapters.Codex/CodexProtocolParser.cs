using System.Text.Json;
using CtrlAgent.Core;

namespace CtrlAgent.Adapters.Codex;

/// <summary>
/// One normalized message from the Codex app-server's JSON-RPC stream.
/// Parsing is kept free of process concerns so it can be unit-tested, the same
/// split <see cref="ClaudeCode"/>'s stream parser uses.
/// <para>
/// Fields the wire may omit stay nullable here rather than being defaulted:
/// the adapter owns the "which thread/turn are we on" state and is the only
/// thing that can fill a gap correctly. A parser that invented
/// <c>"unknown"</c> would hide a protocol change behind a plausible value.
/// </para>
/// </summary>
public abstract record CodexMessage
{
    /// <summary>A thread began. <paramref name="ThreadId"/> is null when the
    /// notification arrived without one — still a real event, just not one
    /// that can be added to the switchable-thread list.</summary>
    public sealed record ThreadStarted(string? ThreadId) : CodexMessage;

    public sealed record TurnStarted(string? ThreadId, string? TurnId) : CodexMessage;

    /// <summary>A turn ended; <paramref name="State"/> is the classification
    /// of its status field.</summary>
    public sealed record TurnFinished(AgentStateKind State, string Summary) : CodexMessage;

    /// <summary>
    /// The server is asking the user something — an approval, or free-form
    /// input. Both block the turn, so both are surfaced, but they carry
    /// different states because only one of them is answered by the approval
    /// bindings on the controller.
    /// </summary>
    public sealed record UserActionRequired(
        string RequestId,
        string Method,
        string? ThreadId,
        string? TurnId,
        string Message,
        AgentStateKind State) : CodexMessage;

    /// <summary>A previously-asked server request no longer needs an answer.</summary>
    public sealed record ServerRequestResolved(string RequestId) : CodexMessage;

    /// <summary>A reply to a request we sent. Exactly one of
    /// <paramref name="Result"/> / <paramref name="Error"/> is meaningful.</summary>
    public sealed record ResponseReceived(long Id, JsonElement Result, string? Error) : CodexMessage;

    public sealed record Ignored : CodexMessage;
}

/// <summary>
/// Classifies app-server traffic. Pure: give it a parsed JSON line, get back
/// what it means.
/// </summary>
public static class CodexProtocolParser
{
    /// <summary>
    /// The app-server asks for free-form input with this method; everything
    /// else that arrives as a server request is an approval gate.
    /// </summary>
    private const string RequestUserInputMethod = "item/tool/requestUserInput";

    public static CodexMessage Parse(JsonElement root)
    {
        // A JSON-RPC frame carrying "method" is inbound work: with an "id" it
        // is a request expecting our answer, without one it is a notification.
        if (root.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString() ?? string.Empty
                : string.Empty;

            return root.TryGetProperty("id", out var requestId)
                ? ParseServerRequest(method, requestId, root)
                : ParseNotification(method, root);
        }

        return ParseResponse(root);
    }

    private static CodexMessage ParseServerRequest(string method, JsonElement idElement, JsonElement root)
    {
        // The id is echoed back verbatim when we answer, and JSON-RPC allows
        // it to be a string or a number — keep the raw text so a numeric id is
        // never re-serialized as a quoted string.
        var requestId = idElement.GetRawText();

        // Best available description, most specific first. Falling back to the
        // method name is deliberate: "item/tool/requestUserInput" on the HUD is
        // ugly but honest, where a generic "Codex needs approval" would hide
        // which gate is open.
        var message =
            TryGetString(root, "params", "reason") ??
            TryGetString(root, "params", "command") ??
            method;

        var state = method.Equals(RequestUserInputMethod, StringComparison.Ordinal)
            ? AgentStateKind.WaitingForInput
            : AgentStateKind.ApprovalRequired;

        return new CodexMessage.UserActionRequired(
            requestId,
            method,
            TryGetString(root, "params", "threadId"),
            TryGetString(root, "params", "turnId"),
            message,
            state);
    }

    private static CodexMessage ParseNotification(string method, JsonElement root) => method switch
    {
        "thread/started" => new CodexMessage.ThreadStarted(TryGetString(root, "params", "thread", "id")),

        "turn/started" => new CodexMessage.TurnStarted(
            TryGetString(root, "params", "threadId"),
            TryGetString(root, "params", "turn", "id")),

        "turn/completed" => ParseTurnCompleted(root),

        "serverRequest/resolved" => TryGetRawText(root, "params", "requestId") is { } resolvedId
            ? new CodexMessage.ServerRequestResolved(resolvedId)
            : new CodexMessage.Ignored(),

        _ => new CodexMessage.Ignored(),
    };

    private static CodexMessage ParseTurnCompleted(JsonElement root)
    {
        var status = TryGetString(root, "params", "turn", "status") ?? "completed";
        var error = TryGetString(root, "params", "turn", "error", "message");

        // A deliberate stop is never an Error — that would fire the error cue
        // for something the user asked for. It reports as AgentInterrupt.State
        // so every adapter answers this identically; see that type for why the
        // answer is Completed rather than Idle.
        if (status.Equals("interrupted", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexMessage.TurnFinished(AgentInterrupt.State, error ?? AgentInterrupt.Message);
        }

        var state = status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            ? AgentStateKind.Error
            : AgentStateKind.Completed;

        return new CodexMessage.TurnFinished(state, error ?? $"Codex turn {status}.");
    }

    private static CodexMessage ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
        {
            return new CodexMessage.Ignored();
        }

        if (root.TryGetProperty("error", out var error))
        {
            return new CodexMessage.ResponseReceived(id, EmptyObject(), error.GetRawText());
        }

        // Cloned because the caller owns the JsonDocument and disposes it as
        // soon as the line is handled.
        var result = root.TryGetProperty("result", out var resultElement)
            ? resultElement.Clone()
            : EmptyObject();

        return new CodexMessage.ResponseReceived(id, result, null);
    }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    private static string? TryGetString(JsonElement element, params string[] path) =>
        Navigate(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string? TryGetRawText(JsonElement element, params string[] path) =>
        Navigate(element, path)?.GetRawText();

    private static JsonElement? Navigate(JsonElement element, string[] path)
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

        return current;
    }
}
