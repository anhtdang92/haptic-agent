using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CtrlAgent.Gui;

/// <summary>
/// The Mainframe pointer, drawn in code rather than shipped as a .cur file —
/// same reasoning as <see cref="BootChime"/>: no binary asset to keep in sync
/// with the theme, and the accent colour lives in exactly one place.
/// <para>
/// It is a targeting reticle: an angular arrowhead with a cyan core, a dark
/// outline so it survives on light content, and a soft glow. Built once and
/// cached; a failure to build one (unsupported backend, headless) leaves the
/// system cursor in place rather than taking the window down.
/// </para>
/// </summary>
internal static class MainframeCursor
{
    private const int Size = 40;

    private static Cursor? _cached;
    private static bool _attempted;

    /// <summary>
    /// The reticle cursor, or <c>null</c> if this platform cannot build one.
    /// Callers should treat null as "keep the default cursor".
    /// </summary>
    internal static Cursor? Reticle
    {
        get
        {
            if (_attempted)
            {
                return _cached;
            }

            _attempted = true;
            try
            {
                _cached = Build();
            }
            catch (Exception)
            {
                // A custom cursor is a flourish. If the backend refuses the
                // bitmap, the pointer simply stays standard.
                _cached = null;
            }

            return _cached;
        }
    }

    private static Cursor Build()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));

        using (var context = bitmap.CreateDrawingContext())
        {
            // Arrowhead: a swept chevron rather than the stock triangle, with
            // a notched tail so it reads as machined.
            var arrow = new PathGeometry();
            using (var figure = arrow.Open())
            {
                figure.BeginFigure(new Point(2, 2), isFilled: true);
                figure.LineTo(new Point(21, 15.5));
                figure.LineTo(new Point(12.5, 17));
                figure.LineTo(new Point(17.5, 26.5));
                figure.LineTo(new Point(12.5, 29));
                figure.LineTo(new Point(8, 19.5));
                figure.LineTo(new Point(2.5, 25));
                figure.EndFigure(isClosed: true);
            }

            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#EAFBFF"), 0),
                    new GradientStop(Color.Parse("#7DD3FC"), 0.55),
                    new GradientStop(Color.Parse("#00A6D6"), 1),
                },
            };

            // Dark keyline first so the pointer stays legible over pale
            // content — a pure cyan arrow disappears on a light panel.
            context.DrawGeometry(fill, new Pen(new SolidColorBrush(Color.Parse("#CC03101E")), 2.4), arrow);

            // Glow pip at the hot spot, so the exact click point is obvious.
            context.DrawEllipse(
                new SolidColorBrush(Color.Parse("#5900D4FF")),
                null,
                new Point(3.5, 3.5),
                5,
                5);
            context.DrawEllipse(new SolidColorBrush(Colors.White), null, new Point(3, 3), 1.4, 1.4);
        }

        // Hot spot sits on the arrow tip, not the bitmap corner.
        return new Cursor(bitmap, new PixelPoint(2, 2));
    }
}
