using System.Text;
using CtrlAgent.Core;

namespace CtrlAgent.Presentation;

/// <summary>
/// Parses controls the way people who hold controllers write them — "LB",
/// "RB", "P1", "D-Up", "Cross" — as well as the formal enum names. The
/// profile editor's modifiers field speaks this so a chord is typed the way
/// the rest of the app prints it ("LB+RB"), not as
/// "LeftShoulder+RightShoulder".
/// </summary>
public static class ControlShorthand
{
    public static bool TryParse(string? text, out ControllerControl control)
    {
        control = ControllerControl.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        control = Normalize(text) switch
        {
            "a" or "cross" => ControllerControl.A,
            "b" or "circle" => ControllerControl.B,
            "x" or "square" => ControllerControl.X,
            "y" or "triangle" => ControllerControl.Y,
            "lb" or "leftbumper" => ControllerControl.LeftShoulder,
            "rb" or "rightbumper" => ControllerControl.RightShoulder,
            "lt" => ControllerControl.LeftTrigger,
            "rt" => ControllerControl.RightTrigger,
            "ls" or "leftstick" or "l3" => ControllerControl.LeftThumbstickButton,
            "rs" or "rightstick" or "r3" => ControllerControl.RightThumbstickButton,
            "p1" => ControllerControl.PaddleLeft1,
            "p2" => ControllerControl.PaddleLeft2,
            "p3" => ControllerControl.PaddleRight1,
            "p4" => ControllerControl.PaddleRight2,
            "start" => ControllerControl.Menu,
            "back" or "select" => ControllerControl.View,
            "xbox" or "ps" or "guide" => ControllerControl.Guide,
            "dup" or "dpadup" => ControllerControl.DPadUp,
            "ddown" or "dpaddown" => ControllerControl.DPadDown,
            "dleft" or "dpadleft" => ControllerControl.DPadLeft,
            "dright" or "dpadright" => ControllerControl.DPadRight,
            _ => ControllerControl.None,
        };

        if (control != ControllerControl.None)
        {
            return true;
        }

        // The formal names still work — pasted JSON fragments and old habits
        // should not become errors.
        if (Enum.TryParse(text.Trim(), ignoreCase: true, out control) &&
            control != ControllerControl.None)
        {
            return true;
        }

        control = ControllerControl.None;
        return false;
    }

    /// <summary>Lowercases and strips separators so "D-Up", "d up" and "D↑"
    /// all read as "dup".</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            switch (character)
            {
                case '-' or '_' or ' ' or '·' or '.':
                    break;
                case '↑':
                    builder.Append("up");
                    break;
                case '↓':
                    builder.Append("down");
                    break;
                case '←':
                    builder.Append("left");
                    break;
                case '→':
                    builder.Append("right");
                    break;
                default:
                    builder.Append(char.ToLowerInvariant(character));
                    break;
            }
        }

        return builder.ToString();
    }
}
