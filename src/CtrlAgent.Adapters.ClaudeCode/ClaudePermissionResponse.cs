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
    /// a session-scoped allow rule for the whole tool so subsequent uses do not
    /// prompt again this session.
    /// </summary>
    public static object Allow(string requestId, string toolName, JsonElement input, bool forSession)
    {
        object decision = forSession
            ? new
            {
                behavior = "allow",
                updatedInput = input,
                updatedPermissions = new object[]
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
