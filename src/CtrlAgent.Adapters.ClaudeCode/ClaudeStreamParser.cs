using System.Text.Json;

namespace CtrlAgent.Adapters.ClaudeCode;

/// <summary>
/// One normalized message from the Claude Code stream-json protocol.
/// Parsing is kept free of process concerns so it can be unit-tested.
/// </summary>
public abstract record ClaudeStreamMessage
{
    public sealed record SessionInit(string SessionId) : ClaudeStreamMessage;

    public sealed record AssistantActivity(string Summary) : ClaudeStreamMessage;

    public sealed record TurnResult(bool IsError, string Summary) : ClaudeStreamMessage;

    public sealed record PermissionRequest(string RequestId, string ToolName, JsonElement Input) : ClaudeStreamMessage;

    public sealed record PermissionCanceled(string RequestId) : ClaudeStreamMessage;

    public sealed record ControlAck(string? RequestId, string? Error) : ClaudeStreamMessage;

    public sealed record Ignored : ClaudeStreamMessage;
}

public static class ClaudeStreamParser
{
    private const int SummaryLength = 160;

    public static ClaudeStreamMessage Parse(JsonElement root)
    {
        var type = GetString(root, "type");

        return type switch
        {
            "system" => ParseSystem(root),
            "assistant" => ParseAssistant(root),
            "result" => ParseResult(root),
            "control_request" => ParseControlRequest(root),
            "control_cancel_request" => ParseControlCancel(root),
            "control_response" => ParseControlResponse(root),
            _ => new ClaudeStreamMessage.Ignored(),
        };
    }

    private static ClaudeStreamMessage ParseSystem(JsonElement root)
    {
        if (GetString(root, "subtype") == "init" &&
            GetString(root, "session_id") is { Length: > 0 } sessionId)
        {
            return new ClaudeStreamMessage.SessionInit(sessionId);
        }

        return new ClaudeStreamMessage.Ignored();
    }

    private static ClaudeStreamMessage ParseAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return new ClaudeStreamMessage.Ignored();
        }

        foreach (var block in content.EnumerateArray())
        {
            switch (GetString(block, "type"))
            {
                case "text" when GetString(block, "text") is { Length: > 0 } text:
                    return new ClaudeStreamMessage.AssistantActivity(Truncate(text));
                case "tool_use" when GetString(block, "name") is { Length: > 0 } tool:
                    return new ClaudeStreamMessage.AssistantActivity($"Using tool: {tool}");
            }
        }

        return new ClaudeStreamMessage.Ignored();
    }

    private static ClaudeStreamMessage ParseResult(JsonElement root)
    {
        var subtype = GetString(root, "subtype") ?? "unknown";
        var isError =
            (root.TryGetProperty("is_error", out var errorFlag) && errorFlag.ValueKind == JsonValueKind.True) ||
            !subtype.Equals("success", StringComparison.Ordinal);
        var summary = GetString(root, "result") is { Length: > 0 } text
            ? Truncate(text)
            : $"Turn finished: {subtype}.";

        return new ClaudeStreamMessage.TurnResult(isError, summary);
    }

    private static ClaudeStreamMessage ParseControlRequest(JsonElement root)
    {
        var requestId = GetString(root, "request_id");
        if (requestId is null ||
            !root.TryGetProperty("request", out var request) ||
            GetString(request, "subtype") != "can_use_tool")
        {
            return new ClaudeStreamMessage.Ignored();
        }

        var toolName = GetString(request, "tool_name") ?? "unknown tool";
        var input = request.TryGetProperty("input", out var inputElement)
            ? inputElement.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();

        return new ClaudeStreamMessage.PermissionRequest(requestId, toolName, input);
    }

    private static ClaudeStreamMessage ParseControlCancel(JsonElement root) =>
        GetString(root, "request_id") is { Length: > 0 } requestId
            ? new ClaudeStreamMessage.PermissionCanceled(requestId)
            : new ClaudeStreamMessage.Ignored();

    private static ClaudeStreamMessage ParseControlResponse(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response))
        {
            return new ClaudeStreamMessage.Ignored();
        }

        var error = GetString(response, "subtype") == "error"
            ? GetString(response, "error") ?? "unknown control error"
            : null;

        return new ClaudeStreamMessage.ControlAck(GetString(response, "request_id"), error);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string text)
    {
        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= SummaryLength ? singleLine : singleLine[..SummaryLength] + "…";
    }
}
