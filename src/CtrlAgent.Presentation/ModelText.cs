namespace CtrlAgent.Presentation;

/// <summary>
/// Chip-sized wording for model names.
/// <para>
/// The adapter reports the model exactly as the CLI states it —
/// "claude-opus-4-7[1m]" — which is the truthful value to track but too long
/// for a status-strip knob. The short form drops the vendor prefix every
/// entry shares and keeps everything that distinguishes one model from
/// another (family, version, context marker). Aliases the user picked
/// ("sonnet", "opus") and the null "not reported yet" state pass through.
/// </para>
/// </summary>
public static class ModelText
{
    public static string Short(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "default";
        }

        var trimmed = model.Trim();
        const string vendorPrefix = "claude-";
        return trimmed.StartsWith(vendorPrefix, StringComparison.OrdinalIgnoreCase) &&
            trimmed.Length > vendorPrefix.Length
            ? trimmed[vendorPrefix.Length..]
            : trimmed;
    }
}
