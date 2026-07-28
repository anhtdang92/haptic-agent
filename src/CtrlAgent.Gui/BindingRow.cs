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

    /// <summary>What this binding does. Needed to decide whether it is
    /// actionable right now — a chord's label alone cannot say that.</summary>
    public required AgentCommandKind Command { get; init; }

    /// <summary>True for a plain d-pad press, which chips render as a drawn
    /// d-pad with the arm lit (<see cref="DPadIcon"/>) — seen, not read.
    /// Chords and gestures keep text; a picture cannot say "LB+".</summary>
    public required bool IsDPad { get; init; }

    /// <summary>The arm to light when <see cref="IsDPad"/>.</summary>
    public required ControllerControl DPadControl { get; init; }

    public static BindingRow From(InputBinding binding) => new()
    {
        Chord = ControlLabels.Chord(binding),
        Action = ControlLabels.Describe(binding),
        IsApproval = binding.RequiresPendingApproval,
        Command = binding.Command,
        IsDPad = binding.Modifiers is not { Count: > 0 } &&
                 binding.Gesture == InputGesture.Press &&
                 binding.Control is ControllerControl.DPadUp or ControllerControl.DPadDown
                     or ControllerControl.DPadLeft or ControllerControl.DPadRight,
        DPadControl = binding.Control,
    };
}
