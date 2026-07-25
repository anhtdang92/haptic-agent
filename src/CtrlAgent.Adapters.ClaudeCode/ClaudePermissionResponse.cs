using System.Text.Json;

namespace CtrlAgent.Adapters.ClaudeCode;

/// <summary>
/// Builds the control_response payloads that answer can_use_tool permission
/// requests. Pure so the wire shapes are unit-testable.
/// </summary>
public static class ClaudePermissionResponse
{
    /// <summary>
    /// Allow the request. With <paramref name="forSession"/>, additionally adds
    /// permission rules so subsequent uses do not prompt again this session —
    /// echoing the CLI's own <paramref name="suggestions"/> when it offered
    /// them (exactly what the Claude app's "always allow" does), otherwise
    /// synthesizing a session-scoped allow rule for the whole tool.
    /// </summary>
    public static object Allow(
        string requestId,
        string toolName,
        JsonElement input,
        bool forSession,
        JsonElement? suggestions = null)
    {
        object decision = forSession
            ? new
            {
                behavior = "allow",
                updatedInput = input,
                updatedPermissions = suggestions is { } suggested
                    ? (object)suggested
                    : new object[]
                    {
                        new
                        {
                            type = "addRules",
                            rules = new object[] { new { toolName } },
                            behavior = "allow",
                            destination = "session",
                        },
                    },
            }
            : new
            {
                behavior = "allow",
                updatedInput = input,
            };

        return Wrap(requestId, decision);
    }

    public static object Deny(string requestId, string message) =>
        Wrap(requestId, new { behavior = "deny", message });

    private static object Wrap(string requestId, object decision) =>
        new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = decision,
            },
        };
}

/// <summary>
/// Builds outbound control_request payloads (client → CLI). Pure so the
/// wire shapes are unit-testable.
/// </summary>
public static class ClaudeControlRequest
{
    public static object Interrupt(string requestId) =>
        new
        {
            type = "control_request",
            request_id = requestId,
            request = new { subtype = "interrupt" },
        };

    /// <summary>Runtime permission-mode switch ("default", "plan", "acceptEdits").</summary>
    public static object SetPermissionMode(string requestId, string mode) =>
        new
        {
            type = "control_request",
            request_id = requestId,
            request = new { subtype = "set_permission_mode", mode },
        };
}
