using System.Text;
using System.Text.Json;

namespace CtrlAgent.Adapters.ClaudeCode;

/// <summary>
/// One normalized message from the Claude Code stream-json protocol.
/// Parsing is kept free of process concerns so it can be unit-tested.
/// </summary>
public abstract record ClaudeStreamMessage
{
    public sealed record SessionInit(
        string SessionId,
        string? Model,
        IReadOnlyList<string> SlashCommands,
        string? McpSummary) : ClaudeStreamMessage;

    public sealed record AssistantActivity(string Summary) : ClaudeStreamMessage;

    /// <summary>A tool finished; the summary is its output snippet.</summary>
    public sealed record ToolResultReceived(string Summary, bool IsError) : ClaudeStreamMessage;

    /// <summary>A streamed chunk of assistant text (requires
    /// <c>--include-partial-messages</c>); consumers accumulate these.</summary>
    public sealed record TextDelta(string Text) : ClaudeStreamMessage;

    /// <summary>The assistant started an extended-thinking block.</summary>
    public sealed record ThinkingStarted : ClaudeStreamMessage;

    public sealed record TurnResult(bool IsError, string Summary) : ClaudeStreamMessage;

    public sealed record PermissionRequest(
        string RequestId,
        string ToolName,
        JsonElement Input,
        JsonElement? Suggestions = null) : ClaudeStreamMessage;

    public sealed record PermissionCanceled(string RequestId) : ClaudeStreamMessage;

    public sealed record ControlAck(string? RequestId, string? Error) : ClaudeStreamMessage;

    public sealed record Ignored : ClaudeStreamMessage;
}

public static class ClaudeStreamParser
{
    private const int SummaryLength = 160;
    private const int ResponseLength = 700;

    public static ClaudeStreamMessage Parse(JsonElement root)
    {
        var type = GetString(root, "type");

        return type switch
        {
            "system" => ParseSystem(root),
            "assistant" => ParseAssistant(root),
            "user" => ParseUserMessage(root),
            "stream_event" => ParseStreamEvent(root),
            "result" => ParseResult(root),
            "control_request" => ParseControlRequest(root),
            "control_cancel_request" => ParseControlCancel(root),
            "control_response" => ParseControlResponse(root),
            _ => new ClaudeStreamMessage.Ignored(),
        };
    }

    private static ClaudeStreamMessage ParseSystem(JsonElement root)
    {
        if (GetString(root, "subtype") != "init" ||
            GetString(root, "session_id") is not { Length: > 0 } sessionId)
        {
            return new ClaudeStreamMessage.Ignored();
        }

        var slashCommands = new List<string>();
        if (root.TryGetProperty("slash_commands", out var commands) &&
            commands.ValueKind == JsonValueKind.Array)
        {
            foreach (var command in commands.EnumerateArray())
            {
                if (command.ValueKind == JsonValueKind.String &&
                    command.GetString() is { Length: > 0 } name)
                {
                    slashCommands.Add(name.StartsWith('/') ? name : "/" + name);
                }
            }
        }

        string? mcpSummary = null;
        if (root.TryGetProperty("mcp_servers", out var servers) &&
            servers.ValueKind == JsonValueKind.Array)
        {
            var total = 0;
            var failed = 0;
            foreach (var server in servers.EnumerateArray())
            {
                total++;
                if (GetString(server, "status") is { } status &&
                    !status.Equals("connected", StringComparison.OrdinalIgnoreCase))
                {
                    failed++;
                }
            }

            if (total > 0)
            {
                mcpSummary = failed > 0
                    ? $"{total} MCP servers, {failed} failed"
                    : $"{total} MCP server{(total == 1 ? string.Empty : "s")}";
            }
        }

        return new ClaudeStreamMessage.SessionInit(sessionId, GetString(root, "model"), slashCommands, mcpSummary);
    }

    /// <summary>
    /// stream-json echoes tool results back as user-role messages; their
    /// output snippets are what the Claude app shows under each tool call.
    /// </summary>
    private static ClaudeStreamMessage ParseUserMessage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return new ClaudeStreamMessage.Ignored();
        }

        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") != "tool_result")
            {
                continue;
            }

            var isError = block.TryGetProperty("is_error", out var errorFlag) &&
                errorFlag.ValueKind == JsonValueKind.True;
            var text = ExtractResultText(block);
            var summary = string.IsNullOrWhiteSpace(text)
                ? (isError ? "Tool failed." : "Tool finished.")
                : text;
            return new ClaudeStreamMessage.ToolResultReceived(Clean(summary, SummaryLength), isError);
        }

        return new ClaudeStreamMessage.Ignored();
    }

    private static string? ExtractResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (GetString(part, "type") == "text" &&
                    GetString(part, "text") is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static ClaudeStreamMessage ParseAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return new ClaudeStreamMessage.Ignored();
        }

        // A message can carry prose and several tool calls; keep the prose
        // when present (it is what the user wants to read), otherwise
        // describe the tool calls concretely.
        var text = new StringBuilder();
        var tools = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            switch (GetString(block, "type"))
            {
                case "text" when GetString(block, "text") is { Length: > 0 } blockText:
                    if (text.Length > 0)
                    {
                        text.Append(' ');
                    }

                    text.Append(blockText);
                    break;

                case "tool_use" when GetString(block, "name") is { Length: > 0 } tool:
                    tools.Add(DescribeTool(tool, block));
                    break;
            }
        }

        if (text.Length > 0)
        {
            return new ClaudeStreamMessage.AssistantActivity(Clean(text.ToString(), ResponseLength));
        }

        return tools.Count > 0
            ? new ClaudeStreamMessage.AssistantActivity(string.Join(" · ", tools.Take(3)))
            : new ClaudeStreamMessage.Ignored();
    }

    /// <summary>
    /// Partial-message events (Anthropic API shape wrapped in
    /// <c>{"type":"stream_event","event":{…}}</c>): text deltas stream the
    /// response as it is written; thinking and tool-use starts give instant
    /// feedback before the full assistant message lands.
    /// </summary>
    private static ClaudeStreamMessage ParseStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var streamEvent))
        {
            return new ClaudeStreamMessage.Ignored();
        }

        switch (GetString(streamEvent, "type"))
        {
            case "content_block_delta":
                if (streamEvent.TryGetProperty("delta", out var delta) &&
                    GetString(delta, "type") == "text_delta" &&
                    GetString(delta, "text") is { Length: > 0 } text)
                {
                    return new ClaudeStreamMessage.TextDelta(text);
                }

                break;

            case "content_block_start":
                if (streamEvent.TryGetProperty("content_block", out var block))
                {
                    switch (GetString(block, "type"))
                    {
                        case "thinking":
                            return new ClaudeStreamMessage.ThinkingStarted();
                        case "tool_use" when GetString(block, "name") is { Length: > 0 } tool:
                            return new ClaudeStreamMessage.AssistantActivity(DescribeTool(tool, block));
                    }
                }

                break;
        }

        return new ClaudeStreamMessage.Ignored();
    }

    private static string DescribeTool(string tool, JsonElement block) =>
        DescribeToolUse(tool, block.TryGetProperty("input", out var input) ? input : default);

    /// <summary>
    /// "Bash: npm test" beats "Using tool: Bash" — surface the input detail
    /// people actually care about, per well-known tool. Also used to render
    /// permission requests concretely ("Write: src/App.cs").
    /// </summary>
    public static string DescribeToolUse(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return $"Using tool: {tool}";
        }

        if (tool.Equals("TodoWrite", StringComparison.OrdinalIgnoreCase))
        {
            return DescribeTodos(input);
        }

        var detail = tool switch
        {
            "Bash" => GetString(input, "command"),
            "Edit" or "Write" or "Read" or "NotebookEdit" => GetString(input, "file_path"),
            "Grep" or "Glob" => GetString(input, "pattern"),
            "WebFetch" => GetString(input, "url"),
            "WebSearch" => GetString(input, "query"),
            "Task" => GetString(input, "description"),
            _ => null,
        };

        return detail is { Length: > 0 }
            ? Clean($"{tool}: {detail}", 90)
            : $"Using tool: {tool}";
    }

    /// <summary>TodoWrite carries the agent's live plan; show its progress.</summary>
    private static string DescribeTodos(JsonElement input)
    {
        if (!input.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array)
        {
            return "Updating the plan";
        }

        var total = 0;
        var done = 0;
        string? active = null;
        foreach (var todo in todos.EnumerateArray())
        {
            total++;
            switch (GetString(todo, "status"))
            {
                case "completed":
                    done++;
                    break;
                case "in_progress":
                    active ??= GetString(todo, "activeForm") ?? GetString(todo, "content");
                    break;
            }
        }

        var progress = $"Plan {done}/{total}";
        return active is { Length: > 0 } ? Clean($"{progress} — {active}", 90) : progress;
    }

    private static ClaudeStreamMessage ParseResult(JsonElement root)
    {
        var subtype = GetString(root, "subtype") ?? "unknown";
        var isError =
            (root.TryGetProperty("is_error", out var errorFlag) && errorFlag.ValueKind == JsonValueKind.True) ||
            !subtype.Equals("success", StringComparison.Ordinal);
        var summary = GetString(root, "result") is { Length: > 0 } text
            ? Clean(text, SummaryLength)
            : $"Turn finished: {subtype}.";

        // Claude-app-style turn stats: how long, how many turns, what it cost.
        var stats = new List<string>();
        if (root.TryGetProperty("duration_ms", out var duration) && duration.TryGetInt64(out var milliseconds))
        {
            stats.Add($"{milliseconds / 1000.0:0.#}s");
        }

        if (root.TryGetProperty("num_turns", out var turns) && turns.TryGetInt32(out var turnCount))
        {
            stats.Add($"{turnCount} turn{(turnCount == 1 ? string.Empty : "s")}");
        }

        if (root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDouble(out var costUsd))
        {
            stats.Add($"${costUsd:0.00##}");
        }

        if (stats.Count > 0)
        {
            summary = $"{summary} ({string.Join(" · ", stats)})";
        }

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

        // The CLI may suggest the exact permission rules an "always allow"
        // should create; echoing them beats synthesizing our own.
        JsonElement? suggestions = null;
        if (request.TryGetProperty("permission_suggestions", out var suggested) &&
            suggested.ValueKind == JsonValueKind.Array &&
            suggested.GetArrayLength() > 0)
        {
            suggestions = suggested.Clone();
        }

        return new ClaudeStreamMessage.PermissionRequest(requestId, toolName, input, suggestions);
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

    private static string Clean(string text, int maxLength)
    {
        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "…";
    }
}
