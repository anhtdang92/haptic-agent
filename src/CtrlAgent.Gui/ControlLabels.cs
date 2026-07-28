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

        // A stick direction is an arrow, not a word: "RS ↑" reads without
        // thinking where "RightThumbstickY (pull)" required a decoder ring.
        // The sign convention is XInput's: positive Y is up, positive X is
        // right. Triggers keep "(pull)" — that is genuinely what you do.
        if (binding.Gesture == InputGesture.AxisThreshold &&
            StickArrow(binding.Control, binding.MinimumValue) is { } arrow)
        {
            return chord + " " + arrow;
        }

        return chord + GestureSuffix(binding.Gesture);
    }

    private static string? StickArrow(ControllerControl control, float threshold) => control switch
    {
        ControllerControl.LeftThumbstickY or ControllerControl.RightThumbstickY =>
            threshold >= 0 ? "↑" : "↓",
        ControllerControl.LeftThumbstickX or ControllerControl.RightThumbstickX =>
            threshold >= 0 ? "→" : "←",
        _ => null,
    };

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
        ControllerControl.Guide => "Xbox/PS",
        ControllerControl.LeftShoulder => "LB",
        ControllerControl.RightShoulder => "RB",
        ControllerControl.LeftTrigger => "LT",
        ControllerControl.RightTrigger => "RT",
        ControllerControl.LeftThumbstickButton => "LS",
        ControllerControl.RightThumbstickButton => "RS",
        ControllerControl.LeftThumbstickX or ControllerControl.LeftThumbstickY => "LS",
        ControllerControl.RightThumbstickX or ControllerControl.RightThumbstickY => "RS",

        // The arrow IS the icon — the d-pad is the one directional cluster on
        // the pad, and "D→" reads without thinking where "D-Right" was a word
        // to parse. Inter covers U+2190–2193, so these can never tofu.
        ControllerControl.DPadUp => "D↑",
        ControllerControl.DPadDown => "D↓",
        ControllerControl.DPadLeft => "D←",
        ControllerControl.DPadRight => "D→",
        ControllerControl.PaddleLeft1 => "P1",
        ControllerControl.PaddleLeft2 => "P2",
        ControllerControl.PaddleRight1 => "P3",
        ControllerControl.PaddleRight2 => "P4",
        _ => control.ToString(),
    };

    /// <summary>SubmitPrompt → "Submit prompt".</summary>
    /// <summary>
    /// What a binding actually does, not just which command it names. Two
    /// bindings can share a command and still differ: the default profile has
    /// A submit the prompt box and LB+A submit a canned prompt, and calling
    /// both "Submit prompt" tells the user they are interchangeable when they
    /// are not.
    /// </summary>
    public static string Describe(InputBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (string.IsNullOrWhiteSpace(binding.Text))
        {
            return Humanize(binding.Command);
        }

        var text = binding.Text.Trim();
        return binding.Command switch
        {
            AgentCommandKind.SubmitPrompt => $"Send \u201c{Shorten(text)}\u201d",
            AgentCommandKind.SetPermissionMode => $"Mode: {text}",
            _ => Humanize(binding.Command),
        };
    }

    private static string Shorten(string text)
    {
        const int limit = 34;
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= limit ? single : single[..limit].TrimEnd() + "\u2026";
    }

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
