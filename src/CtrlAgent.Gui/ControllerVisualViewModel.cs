using Avalonia.Media;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>
/// Mirrors the physical controller: per-control brushes (pressed cyan beats
/// approval-highlight amber beats idle), trigger pulls as bar widths, stick
/// deflection as knob offsets, and face-button labels that follow the
/// connected controller's flavor (ABXY vs PlayStation shapes). Fed from the
/// engine's raw input stream on the UI thread.
/// </summary>
public sealed class ControllerVisualViewModel : ViewModelBase
{
    private const double TriggerBarWidth = 66;
    private const double StickTravel = 9;

    private static readonly IBrush PressedBrush = new SolidColorBrush(Color.Parse("#00D4FF"));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.Parse("#FFB020"));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#22314F"));

    private static readonly ControllerControl[] VisualControls =
    [
        ControllerControl.A,
        ControllerControl.B,
        ControllerControl.X,
        ControllerControl.Y,
        ControllerControl.Menu,
        ControllerControl.View,
        ControllerControl.Guide,
        ControllerControl.DPadUp,
        ControllerControl.DPadDown,
        ControllerControl.DPadLeft,
        ControllerControl.DPadRight,
        ControllerControl.LeftShoulder,
        ControllerControl.RightShoulder,
        ControllerControl.LeftThumbstickButton,
        ControllerControl.RightThumbstickButton,
        ControllerControl.PaddleLeft1,
        ControllerControl.PaddleLeft2,
        ControllerControl.PaddleRight1,
        ControllerControl.PaddleRight2,
    ];

    private readonly HashSet<ControllerControl> _pressed = [];
    private readonly HashSet<ControllerControl> _highlighted = [];
    private float _leftTrigger, _rightTrigger;
    private float _leftStickX, _leftStickY, _rightStickX, _rightStickY;
    private string _southLabel = "A";
    private string _eastLabel = "B";
    private string _westLabel = "X";
    private string _northLabel = "Y";

    public IBrush ABrush => BrushFor(ControllerControl.A);

    public IBrush BBrush => BrushFor(ControllerControl.B);

    public IBrush XBrush => BrushFor(ControllerControl.X);

    public IBrush YBrush => BrushFor(ControllerControl.Y);

    public IBrush MenuBrush => BrushFor(ControllerControl.Menu);

    public IBrush ViewBrush => BrushFor(ControllerControl.View);

    public IBrush GuideBrush => BrushFor(ControllerControl.Guide);

    public IBrush DPadUpBrush => BrushFor(ControllerControl.DPadUp);

    public IBrush DPadDownBrush => BrushFor(ControllerControl.DPadDown);

    public IBrush DPadLeftBrush => BrushFor(ControllerControl.DPadLeft);

    public IBrush DPadRightBrush => BrushFor(ControllerControl.DPadRight);

    public IBrush LeftShoulderBrush => BrushFor(ControllerControl.LeftShoulder);

    public IBrush RightShoulderBrush => BrushFor(ControllerControl.RightShoulder);

    public IBrush LeftThumbBrush => BrushFor(ControllerControl.LeftThumbstickButton);

    public IBrush RightThumbBrush => BrushFor(ControllerControl.RightThumbstickButton);

    public IBrush PaddleLeft1Brush => BrushFor(ControllerControl.PaddleLeft1);

    public IBrush PaddleLeft2Brush => BrushFor(ControllerControl.PaddleLeft2);

    public IBrush PaddleRight1Brush => BrushFor(ControllerControl.PaddleRight1);

    public IBrush PaddleRight2Brush => BrushFor(ControllerControl.PaddleRight2);

    public string SouthLabel { get => _southLabel; private set => Set(ref _southLabel, value); }

    public string EastLabel { get => _eastLabel; private set => Set(ref _eastLabel, value); }

    public string WestLabel { get => _westLabel; private set => Set(ref _westLabel, value); }

    public string NorthLabel { get => _northLabel; private set => Set(ref _northLabel, value); }

    public double LeftTriggerWidth => Math.Clamp(_leftTrigger, 0f, 1f) * TriggerBarWidth;

    public double RightTriggerWidth => Math.Clamp(_rightTrigger, 0f, 1f) * TriggerBarWidth;

    public double LeftStickOffsetX => Math.Clamp(_leftStickX, -1f, 1f) * StickTravel;

    public double LeftStickOffsetY => -Math.Clamp(_leftStickY, -1f, 1f) * StickTravel;

    public double RightStickOffsetX => Math.Clamp(_rightStickX, -1f, 1f) * StickTravel;

    public double RightStickOffsetY => -Math.Clamp(_rightStickY, -1f, 1f) * StickTravel;

    public void Apply(ControllerInputEvent inputEvent)
    {
        switch (inputEvent.Kind)
        {
            case ControllerInputEventKind.Pressed:
                if (_pressed.Add(inputEvent.Control))
                {
                    RaiseBrush(inputEvent.Control);
                }

                break;

            case ControllerInputEventKind.Released:
                if (_pressed.Remove(inputEvent.Control))
                {
                    RaiseBrush(inputEvent.Control);
                }

                break;

            case ControllerInputEventKind.ValueChanged:
                SetAxis(inputEvent.Control, inputEvent.Value);
                break;

            case ControllerInputEventKind.Disconnected:
                Reset();
                break;
        }
    }

    /// <summary>
    /// Highlights the physical controls that can answer the pending approval
    /// (pass null or empty to clear). The controls come from the live profile,
    /// so remapping changes what glows.
    /// </summary>
    public void SetApprovalHighlight(IReadOnlyCollection<ControllerControl>? controls)
    {
        _highlighted.Clear();
        if (controls is not null)
        {
            foreach (var control in controls)
            {
                _highlighted.Add(control);
            }
        }

        RaiseAllBrushes();
    }

    /// <summary>Switches face-button labels between ABXY and PlayStation shapes.</summary>
    public void SetPlayStationFlavor(bool isPlayStation)
    {
        SouthLabel = isPlayStation ? "✕" : "A";
        EastLabel = isPlayStation ? "◯" : "B";
        WestLabel = isPlayStation ? "▢" : "X";
        NorthLabel = isPlayStation ? "△" : "Y";
    }

    public void Reset()
    {
        _pressed.Clear();
        _leftTrigger = 0f;
        _rightTrigger = 0f;
        _leftStickX = 0f;
        _leftStickY = 0f;
        _rightStickX = 0f;
        _rightStickY = 0f;
        RaiseAllBrushes();
        Raise(nameof(LeftTriggerWidth));
        Raise(nameof(RightTriggerWidth));
        Raise(nameof(LeftStickOffsetX));
        Raise(nameof(LeftStickOffsetY));
        Raise(nameof(RightStickOffsetX));
        Raise(nameof(RightStickOffsetY));
    }

    private IBrush BrushFor(ControllerControl control) =>
        _pressed.Contains(control) ? PressedBrush :
        _highlighted.Contains(control) ? HighlightBrush :
        IdleBrush;

    private void SetAxis(ControllerControl control, float value)
    {
        switch (control)
        {
            case ControllerControl.LeftTrigger:
                _leftTrigger = value;
                Raise(nameof(LeftTriggerWidth));
                break;
            case ControllerControl.RightTrigger:
                _rightTrigger = value;
                Raise(nameof(RightTriggerWidth));
                break;
            case ControllerControl.LeftThumbstickX:
                _leftStickX = value;
                Raise(nameof(LeftStickOffsetX));
                break;
            case ControllerControl.LeftThumbstickY:
                _leftStickY = value;
                Raise(nameof(LeftStickOffsetY));
                break;
            case ControllerControl.RightThumbstickX:
                _rightStickX = value;
                Raise(nameof(RightStickOffsetX));
                break;
            case ControllerControl.RightThumbstickY:
                _rightStickY = value;
                Raise(nameof(RightStickOffsetY));
                break;
        }
    }

    private void RaiseBrush(ControllerControl control)
    {
        var name = control switch
        {
            ControllerControl.A => nameof(ABrush),
            ControllerControl.B => nameof(BBrush),
            ControllerControl.X => nameof(XBrush),
            ControllerControl.Y => nameof(YBrush),
            ControllerControl.Menu => nameof(MenuBrush),
            ControllerControl.View => nameof(ViewBrush),
            ControllerControl.Guide => nameof(GuideBrush),
            ControllerControl.DPadUp => nameof(DPadUpBrush),
            ControllerControl.DPadDown => nameof(DPadDownBrush),
            ControllerControl.DPadLeft => nameof(DPadLeftBrush),
            ControllerControl.DPadRight => nameof(DPadRightBrush),
            ControllerControl.LeftShoulder => nameof(LeftShoulderBrush),
            ControllerControl.RightShoulder => nameof(RightShoulderBrush),
            ControllerControl.LeftThumbstickButton => nameof(LeftThumbBrush),
            ControllerControl.RightThumbstickButton => nameof(RightThumbBrush),
            ControllerControl.PaddleLeft1 => nameof(PaddleLeft1Brush),
            ControllerControl.PaddleLeft2 => nameof(PaddleLeft2Brush),
            ControllerControl.PaddleRight1 => nameof(PaddleRight1Brush),
            ControllerControl.PaddleRight2 => nameof(PaddleRight2Brush),
            _ => null,
        };

        if (name is not null)
        {
            Raise(name);
        }
    }

    private void RaiseAllBrushes()
    {
        foreach (var control in VisualControls)
        {
            RaiseBrush(control);
        }
    }
}
