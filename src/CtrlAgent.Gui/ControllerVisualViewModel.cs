using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>Converts a pressed flag into the neon-lit or idle control brush.</summary>
public sealed class PressedBrushConverter : IValueConverter
{
    public static readonly PressedBrushConverter Instance = new();

    private static readonly IBrush PressedBrush = new SolidColorBrush(Color.Parse("#00D4FF"));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#22314F"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? PressedBrush : IdleBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Mirrors the physical controller: booleans per button/paddle, trigger pulls
/// as bar widths, and stick deflection as knob offsets. Fed from the engine's
/// raw input stream on the UI thread.
/// </summary>
public sealed class ControllerVisualViewModel : ViewModelBase
{
    private const double TriggerBarWidth = 66;
    private const double StickTravel = 9;

    private bool _a, _b, _x, _y;
    private bool _menu, _view;
    private bool _dPadUp, _dPadDown, _dPadLeft, _dPadRight;
    private bool _leftShoulder, _rightShoulder;
    private bool _leftThumb, _rightThumb;
    private bool _paddleLeft1, _paddleLeft2, _paddleRight1, _paddleRight2;
    private float _leftTrigger, _rightTrigger;
    private float _leftStickX, _leftStickY, _rightStickX, _rightStickY;

    public bool IsAPressed { get => _a; private set => Set(ref _a, value); }

    public bool IsBPressed { get => _b; private set => Set(ref _b, value); }

    public bool IsXPressed { get => _x; private set => Set(ref _x, value); }

    public bool IsYPressed { get => _y; private set => Set(ref _y, value); }

    public bool IsMenuPressed { get => _menu; private set => Set(ref _menu, value); }

    public bool IsViewPressed { get => _view; private set => Set(ref _view, value); }

    public bool IsDPadUpPressed { get => _dPadUp; private set => Set(ref _dPadUp, value); }

    public bool IsDPadDownPressed { get => _dPadDown; private set => Set(ref _dPadDown, value); }

    public bool IsDPadLeftPressed { get => _dPadLeft; private set => Set(ref _dPadLeft, value); }

    public bool IsDPadRightPressed { get => _dPadRight; private set => Set(ref _dPadRight, value); }

    public bool IsLeftShoulderPressed { get => _leftShoulder; private set => Set(ref _leftShoulder, value); }

    public bool IsRightShoulderPressed { get => _rightShoulder; private set => Set(ref _rightShoulder, value); }

    public bool IsLeftThumbPressed { get => _leftThumb; private set => Set(ref _leftThumb, value); }

    public bool IsRightThumbPressed { get => _rightThumb; private set => Set(ref _rightThumb, value); }

    public bool IsPaddleLeft1Pressed { get => _paddleLeft1; private set => Set(ref _paddleLeft1, value); }

    public bool IsPaddleLeft2Pressed { get => _paddleLeft2; private set => Set(ref _paddleLeft2, value); }

    public bool IsPaddleRight1Pressed { get => _paddleRight1; private set => Set(ref _paddleRight1, value); }

    public bool IsPaddleRight2Pressed { get => _paddleRight2; private set => Set(ref _paddleRight2, value); }

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
                SetButton(inputEvent.Control, true);
                break;

            case ControllerInputEventKind.Released:
                SetButton(inputEvent.Control, false);
                break;

            case ControllerInputEventKind.ValueChanged:
                SetAxis(inputEvent.Control, inputEvent.Value);
                break;

            case ControllerInputEventKind.Disconnected:
                Reset();
                break;
        }
    }

    public void Reset()
    {
        SetButton(ControllerControl.A, false);
        SetButton(ControllerControl.B, false);
        SetButton(ControllerControl.X, false);
        SetButton(ControllerControl.Y, false);
        SetButton(ControllerControl.Menu, false);
        SetButton(ControllerControl.View, false);
        SetButton(ControllerControl.DPadUp, false);
        SetButton(ControllerControl.DPadDown, false);
        SetButton(ControllerControl.DPadLeft, false);
        SetButton(ControllerControl.DPadRight, false);
        SetButton(ControllerControl.LeftShoulder, false);
        SetButton(ControllerControl.RightShoulder, false);
        SetButton(ControllerControl.LeftThumbstickButton, false);
        SetButton(ControllerControl.RightThumbstickButton, false);
        SetButton(ControllerControl.PaddleLeft1, false);
        SetButton(ControllerControl.PaddleLeft2, false);
        SetButton(ControllerControl.PaddleRight1, false);
        SetButton(ControllerControl.PaddleRight2, false);
        SetAxis(ControllerControl.LeftTrigger, 0f);
        SetAxis(ControllerControl.RightTrigger, 0f);
        SetAxis(ControllerControl.LeftThumbstickX, 0f);
        SetAxis(ControllerControl.LeftThumbstickY, 0f);
        SetAxis(ControllerControl.RightThumbstickX, 0f);
        SetAxis(ControllerControl.RightThumbstickY, 0f);
    }

    private void SetButton(ControllerControl control, bool pressed)
    {
        switch (control)
        {
            case ControllerControl.A: IsAPressed = pressed; break;
            case ControllerControl.B: IsBPressed = pressed; break;
            case ControllerControl.X: IsXPressed = pressed; break;
            case ControllerControl.Y: IsYPressed = pressed; break;
            case ControllerControl.Menu: IsMenuPressed = pressed; break;
            case ControllerControl.View: IsViewPressed = pressed; break;
            case ControllerControl.DPadUp: IsDPadUpPressed = pressed; break;
            case ControllerControl.DPadDown: IsDPadDownPressed = pressed; break;
            case ControllerControl.DPadLeft: IsDPadLeftPressed = pressed; break;
            case ControllerControl.DPadRight: IsDPadRightPressed = pressed; break;
            case ControllerControl.LeftShoulder: IsLeftShoulderPressed = pressed; break;
            case ControllerControl.RightShoulder: IsRightShoulderPressed = pressed; break;
            case ControllerControl.LeftThumbstickButton: IsLeftThumbPressed = pressed; break;
            case ControllerControl.RightThumbstickButton: IsRightThumbPressed = pressed; break;
            case ControllerControl.PaddleLeft1: IsPaddleLeft1Pressed = pressed; break;
            case ControllerControl.PaddleLeft2: IsPaddleLeft2Pressed = pressed; break;
            case ControllerControl.PaddleRight1: IsPaddleRight1Pressed = pressed; break;
            case ControllerControl.PaddleRight2: IsPaddleRight2Pressed = pressed; break;
        }
    }

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
}
