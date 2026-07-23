using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>
/// One row of the active-bindings list: a chord chip, the action it triggers,
/// and whether it is approval-gated (shown as an amber tag).
/// </summary>
public sealed class BindingRow
{
    public required string Chord { get; init; }

    public required string Action { get; init; }

    public required bool IsApproval { get; init; }

    public static BindingRow From(InputBinding binding) => new()
    {
        Chord = ControlLabels.Chord(binding),
        Action = ControlLabels.Humanize(binding.Command),
        IsApproval = binding.RequiresPendingApproval,
    };
}
