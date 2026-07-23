using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>
/// Compact display names for controls, chords, and gestures, shared by
/// CTRL·BOT's coaching hints and the active-bindings list so both speak the
/// same shorthand (A, RB+A, P1, D-Left …).
/// </summary>
internal static class ControlLabels
{
    public static string Chord(InputBinding binding)
    {
        var chord = binding.Modifiers is { Count: > 0 }
            ? string.Join("+", binding.Modifiers.OrderBy(modifier => modifier).Select(Label)) + "+" + Label(binding.Control)
            : Label(binding.Control);

        return chord + GestureSuffix(binding.Gesture);
    }

    public static string GestureSuffix(InputGesture gesture) => gesture switch
    {
        InputGesture.Tap => " (tap)",
        InputGesture.Hold => " (hold)",
        InputGesture.DoublePress => " (double)",
        InputGesture.AxisThreshold => " (pull)",
        InputGesture.Release => " (release)",
        _ => string.Empty,
    };

    public static string Label(ControllerControl control) => control switch
    {
        ControllerControl.A => "A",
        ControllerControl.B => "B",
        ControllerControl.X => "X",
        ControllerControl.Y => "Y",
        ControllerControl.Menu => "Menu",
        ControllerControl.View => "View",
        ControllerControl.LeftShoulder => "LB",
        ControllerControl.RightShoulder => "RB",
        ControllerControl.LeftTrigger => "LT",
        ControllerControl.RightTrigger => "RT",
        ControllerControl.LeftThumbstickButton => "LS",
        ControllerControl.RightThumbstickButton => "RS",
        ControllerControl.DPadUp => "D-Up",
        ControllerControl.DPadDown => "D-Down",
        ControllerControl.DPadLeft => "D-Left",
        ControllerControl.DPadRight => "D-Right",
        ControllerControl.PaddleLeft1 => "P1",
        ControllerControl.PaddleLeft2 => "P2",
        ControllerControl.PaddleRight1 => "P3",
        ControllerControl.PaddleRight2 => "P4",
        _ => control.ToString(),
    };

    /// <summary>SubmitPrompt → "Submit prompt".</summary>
    public static string Humanize(AgentCommandKind command)
    {
        var name = command.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append(' ');
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
