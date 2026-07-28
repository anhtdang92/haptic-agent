using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>
/// A literal picture of the d-pad with one arm lit — for shortcut chips, so
/// "press d-pad left" is seen, not read. Code-drawn like every icon in the
/// app; the cross scales to whatever size the chip gives it.
/// </summary>
public sealed class DPadIcon : Control
{
    public static readonly StyledProperty<ControllerControl> DirectionProperty =
        AvaloniaProperty.Register<DPadIcon, ControllerControl>(nameof(Direction), ControllerControl.DPadLeft);

    private static readonly Geometry Cross = Geometry.Parse(
        "M7,2.2 H11 V7 H15.8 V11 H11 V15.8 H7 V11 H2.2 V7 H7 Z");

    private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.Parse("#8090A8CC")), 1.3);
    private static readonly IBrush Lit = new SolidColorBrush(Color.Parse("#7DD3FC"));
    private static readonly IBrush Center = new SolidColorBrush(Color.Parse("#4D90A8CC"));

    static DPadIcon()
    {
        AffectsRender<DPadIcon>(DirectionProperty);
    }

    /// <summary>Which arm is lit. Only the four d-pad values are meaningful.</summary>
    public ControllerControl Direction
    {
        get => GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 18.0;
        if (scale <= 0)
        {
            return;
        }

        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, Outline, Cross);

            var arm = Direction switch
            {
                ControllerControl.DPadUp => new Rect(7.7, 3.0, 2.6, 4.2),
                ControllerControl.DPadDown => new Rect(7.7, 10.8, 2.6, 4.2),
                ControllerControl.DPadRight => new Rect(10.8, 7.7, 4.2, 2.6),
                _ => new Rect(3.0, 7.7, 4.2, 2.6),
            };
            context.DrawRectangle(Lit, null, new RoundedRect(arm, 1.1));
            context.DrawEllipse(Center, null, new Point(9, 9), 1.2, 1.2);
        }
    }
}
