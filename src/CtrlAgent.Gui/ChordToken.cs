using Avalonia.Media;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>
/// One piece of a chord, rendered the way it looks on the pad: face buttons
/// as their colored circles (A green, B red, X blue, Y yellow — the Xbox
/// shell colors the legend already uses), the d-pad as the drawn cross with
/// its arm lit, everything else as keycap text. "RB+A" should read as
/// <em>bumper plus the green button</em>, not as three characters.
/// </summary>
public sealed class ChordToken
{
    private static readonly IBrush GreenA = new SolidColorBrush(Color.Parse("#6EBE45"));
    private static readonly IBrush RedB = new SolidColorBrush(Color.Parse("#E85555"));
    private static readonly IBrush BlueX = new SolidColorBrush(Color.Parse("#4A9BE8"));
    private static readonly IBrush YellowY = new SolidColorBrush(Color.Parse("#E8C531"));

    public required string Text { get; init; }

    public bool IsFace { get; init; }

    public bool IsDPad { get; init; }

    public bool IsText => !IsFace && !IsDPad;

    /// <summary>The control behind a face or d-pad token.</summary>
    public ControllerControl Control { get; init; }

    /// <summary>Ring and letter color for a face-button token.</summary>
    public IBrush FaceBrush { get; init; } = GreenA;

    /// <summary>Tokens for one binding's chord, physical parts in press order.</summary>
    public static IReadOnlyList<ChordToken> For(InputBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var controls = new List<ControllerControl>();
        if (binding.Modifiers is { Count: > 0 })
        {
            controls.AddRange(binding.Modifiers.OrderBy(modifier => modifier));
        }

        controls.Add(binding.Control);

        var tokens = new List<ChordToken>();
        for (var index = 0; index < controls.Count; index++)
        {
            if (index > 0)
            {
                tokens.Add(new ChordToken { Text = "+" });
            }

            tokens.Add(TokenFor(controls[index]));
        }

        if (binding.Gesture == InputGesture.AxisThreshold &&
            ControlLabels.StickArrow(binding.Control, binding.MinimumValue) is { } arrow)
        {
            tokens.Add(new ChordToken { Text = arrow });
        }
        else if (ControlLabels.GestureSuffix(binding.Gesture).Trim() is { Length: > 0 } suffix)
        {
            tokens.Add(new ChordToken { Text = suffix });
        }

        return tokens;
    }

    /// <summary>Several chords merged into one row: "P1 · RB+A".</summary>
    public static IReadOnlyList<ChordToken> Join(IEnumerable<IReadOnlyList<ChordToken>> chords)
    {
        ArgumentNullException.ThrowIfNull(chords);

        var tokens = new List<ChordToken>();
        foreach (var chord in chords)
        {
            if (tokens.Count > 0)
            {
                tokens.Add(new ChordToken { Text = "·" });
            }

            tokens.AddRange(chord);
        }

        return tokens;
    }

    private static ChordToken TokenFor(ControllerControl control) => control switch
    {
        ControllerControl.A => new ChordToken { Text = "A", IsFace = true, Control = control, FaceBrush = GreenA },
        ControllerControl.B => new ChordToken { Text = "B", IsFace = true, Control = control, FaceBrush = RedB },
        ControllerControl.X => new ChordToken { Text = "X", IsFace = true, Control = control, FaceBrush = BlueX },
        ControllerControl.Y => new ChordToken { Text = "Y", IsFace = true, Control = control, FaceBrush = YellowY },
        ControllerControl.DPadUp or ControllerControl.DPadDown or
        ControllerControl.DPadLeft or ControllerControl.DPadRight =>
            new ChordToken { Text = string.Empty, IsDPad = true, Control = control },
        _ => new ChordToken { Text = ControlLabels.Label(control) },
    };
}
