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

    private static readonly IBrush CrossBlue = new SolidColorBrush(Color.Parse("#8FB7FF"));
    private static readonly IBrush CircleRed = new SolidColorBrush(Color.Parse("#FF6F79"));
    private static readonly IBrush SquarePink = new SolidColorBrush(Color.Parse("#E8A0DC"));
    private static readonly IBrush TriangleTeal = new SolidColorBrush(Color.Parse("#63D9B2"));

    private static readonly Geometry CrossShape = Geometry.Parse("M2.4,2.4 L9.6,9.6 M9.6,2.4 L2.4,9.6");
    private static readonly Geometry CircleShape = Geometry.Parse("M6,1.8 A4.2,4.2 0 1 1 5.99,1.8");
    private static readonly Geometry SquareShape = Geometry.Parse("M2.4,2.4 H9.6 V9.6 H2.4 Z");
    private static readonly Geometry TriangleShape = Geometry.Parse("M6,1.9 L10.2,9.7 H1.8 Z");

    /// <summary>
    /// True while the connected pad is a DualSense, set alongside the mirror's
    /// flavor switch. Face tokens then render Sony's shapes in Sony's colors
    /// (positional mapping: A→✕, B→○, X→□, Y→△ — the same mapping the
    /// DualSense adapter uses) as drawn geometry, because the bundled font has
    /// no shape glyphs. App-global like the mic preference: which pad is
    /// plugged in is a fact about the machine.
    /// </summary>
    public static bool PlayStation { get; set; }

    public required string Text { get; init; }

    public bool IsFace { get; init; }

    public bool IsDPad { get; init; }

    public bool IsText => !IsFace && !IsDPad;

    /// <summary>The control behind a face or d-pad token.</summary>
    public ControllerControl Control { get; init; }

    /// <summary>Ring and letter color for a face-button token.</summary>
    public IBrush FaceBrush { get; init; } = GreenA;

    /// <summary>Sony shape geometry when the pad is a DualSense; null renders
    /// the letter instead.</summary>
    public Geometry? ShapeData { get; init; }

    public bool IsShape => ShapeData is not null;

    public bool IsLetter => IsFace && ShapeData is null;

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

    /// <summary>One control's token on its own — the Mainframe legend uses
    /// these so its A/B/Y chips flip to Sony shapes with the rest of the
    /// chip language instead of contradicting the pad on screen.</summary>
    public static ChordToken Single(ControllerControl control) => TokenFor(control);

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
        ControllerControl.A when PlayStation =>
            new ChordToken { Text = string.Empty, IsFace = true, Control = control, FaceBrush = CrossBlue, ShapeData = CrossShape },
        ControllerControl.B when PlayStation =>
            new ChordToken { Text = string.Empty, IsFace = true, Control = control, FaceBrush = CircleRed, ShapeData = CircleShape },
        ControllerControl.X when PlayStation =>
            new ChordToken { Text = string.Empty, IsFace = true, Control = control, FaceBrush = SquarePink, ShapeData = SquareShape },
        ControllerControl.Y when PlayStation =>
            new ChordToken { Text = string.Empty, IsFace = true, Control = control, FaceBrush = TriangleTeal, ShapeData = TriangleShape },
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
