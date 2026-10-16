using AppleMusicOverlay.Models;
using Windows.Gaming.Input;

namespace AppleMusicOverlay.Services;

public sealed class RawGamepadButtonReader
{
    private static readonly GamepadButton[] DualSenseButtons =
    [
        GamepadButton.FaceWest, GamepadButton.FaceSouth, GamepadButton.FaceEast, GamepadButton.FaceNorth,
        GamepadButton.LeftShoulder, GamepadButton.RightShoulder, GamepadButton.LeftTrigger, GamepadButton.RightTrigger,
        GamepadButton.View, GamepadButton.Menu, GamepadButton.LeftStick, GamepadButton.RightStick
    ];
    private readonly GamepadButton?[] _buttons;
    private readonly bool _dualSenseLayout;
    private bool _leftTriggerPressed;
    private bool _rightTriggerPressed;

    public RawGamepadButtonReader(IReadOnlyList<GameControllerButtonLabel> labels, bool dualSenseLayout = false)
    {
        _dualSenseLayout = dualSenseLayout;
        _buttons = labels.Select((label, index) => MapLabel(label) ??
            (dualSenseLayout && index < DualSenseButtons.Length ? DualSenseButtons[index] : (GamepadButton?)null)).ToArray();
    }

    public IReadOnlySet<GamepadButton> Read(
        ReadOnlySpan<bool> buttons,
        GameControllerSwitchPosition direction,
        ReadOnlySpan<double> axes)
    {
        HashSet<GamepadButton> pressed = new();
        for (int index = 0; index < Math.Min(buttons.Length, _buttons.Length); index++)
        {
            if (buttons[index] && _buttons[index] is { } button)
            {
                pressed.Add(button);
            }
        }

        if (direction is GameControllerSwitchPosition.Up or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.UpRight)
            pressed.Add(GamepadButton.DPadUp);
        if (direction is GameControllerSwitchPosition.Down or GameControllerSwitchPosition.DownLeft or GameControllerSwitchPosition.DownRight)
            pressed.Add(GamepadButton.DPadDown);
        if (direction is GameControllerSwitchPosition.Left or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.DownLeft)
            pressed.Add(GamepadButton.DPadLeft);
        if (direction is GameControllerSwitchPosition.Right or GameControllerSwitchPosition.UpRight or GameControllerSwitchPosition.DownRight)
            pressed.Add(GamepadButton.DPadRight);

        // Sony 054C:0CE6/0DF2 HID layout: X, Y, Z, Rx (L2), Ry (R2), Rz.
        // Apply this axis layout only to the known DualSense devices, never to generic controllers.
        if (_dualSenseLayout && axes.Length >= 6)
        {
            _leftTriggerPressed = GamepadButtonReader.UpdateAnalogButton(_leftTriggerPressed, axes[3]);
            _rightTriggerPressed = GamepadButtonReader.UpdateAnalogButton(_rightTriggerPressed, axes[4]);
            pressed.Remove(GamepadButton.LeftTrigger);
            pressed.Remove(GamepadButton.RightTrigger);
            if (_leftTriggerPressed) pressed.Add(GamepadButton.LeftTrigger);
            if (_rightTriggerPressed) pressed.Add(GamepadButton.RightTrigger);
        }

        return pressed;
    }

    private static GamepadButton? MapLabel(GameControllerButtonLabel label) => label switch
    {
        GameControllerButtonLabel.Cross or GameControllerButtonLabel.XboxA => GamepadButton.FaceSouth,
        GameControllerButtonLabel.Circle or GameControllerButtonLabel.XboxB => GamepadButton.FaceEast,
        GameControllerButtonLabel.Square or GameControllerButtonLabel.XboxX => GamepadButton.FaceWest,
        GameControllerButtonLabel.Triangle or GameControllerButtonLabel.XboxY => GamepadButton.FaceNorth,
        GameControllerButtonLabel.LeftBumper or GameControllerButtonLabel.Left1 or GameControllerButtonLabel.XboxLeftBumper => GamepadButton.LeftShoulder,
        GameControllerButtonLabel.RightBumper or GameControllerButtonLabel.Right1 or GameControllerButtonLabel.XboxRightBumper => GamepadButton.RightShoulder,
        GameControllerButtonLabel.LeftTrigger or GameControllerButtonLabel.Left2 or GameControllerButtonLabel.XboxLeftTrigger => GamepadButton.LeftTrigger,
        GameControllerButtonLabel.RightTrigger or GameControllerButtonLabel.Right2 or GameControllerButtonLabel.XboxRightTrigger => GamepadButton.RightTrigger,
        GameControllerButtonLabel.LeftStickButton or GameControllerButtonLabel.Left3 or GameControllerButtonLabel.XboxLeftStickButton => GamepadButton.LeftStick,
        GameControllerButtonLabel.RightStickButton or GameControllerButtonLabel.Right3 or GameControllerButtonLabel.XboxRightStickButton => GamepadButton.RightStick,
        GameControllerButtonLabel.View or GameControllerButtonLabel.Back or GameControllerButtonLabel.Share or GameControllerButtonLabel.Select or GameControllerButtonLabel.XboxView or GameControllerButtonLabel.XboxBack => GamepadButton.View,
        GameControllerButtonLabel.Menu or GameControllerButtonLabel.Options or GameControllerButtonLabel.Start or GameControllerButtonLabel.XboxMenu or GameControllerButtonLabel.XboxStart => GamepadButton.Menu,
        GameControllerButtonLabel.Up or GameControllerButtonLabel.XboxUp => GamepadButton.DPadUp,
        GameControllerButtonLabel.Down or GameControllerButtonLabel.XboxDown => GamepadButton.DPadDown,
        GameControllerButtonLabel.Left or GameControllerButtonLabel.XboxLeft => GamepadButton.DPadLeft,
        GameControllerButtonLabel.Right or GameControllerButtonLabel.XboxRight => GamepadButton.DPadRight,
        _ => null
    };
}

internal sealed class RawGamepadInput
{
    private readonly RawGameController _controller;
    private readonly RawGamepadButtonReader _reader;
    private readonly bool[] _buttons;
    private readonly GameControllerSwitchPosition[] _switches;
    private readonly double[] _axes;
    private readonly int _directionIndex;

    public RawGamepadInput(RawGameController controller)
    {
        _controller = controller;
        _buttons = new bool[controller.ButtonCount];
        _switches = new GameControllerSwitchPosition[controller.SwitchCount];
        _axes = new double[controller.AxisCount];
        var labels = Enumerable.Range(0, _buttons.Length).Select(controller.GetButtonLabel).ToArray();
        bool dualSense = controller.HardwareVendorId == 0x054C && controller.HardwareProductId is 0x0CE6 or 0x0DF2;
        _reader = new RawGamepadButtonReader(labels, dualSense);
        _directionIndex = Enumerable.Range(0, _switches.Length).FirstOrDefault(
            index => controller.GetSwitchKind(index) is GameControllerSwitchKind.FourWay or GameControllerSwitchKind.EightWay, -1);
    }

    public IReadOnlySet<GamepadButton> Read()
    {
        _controller.GetCurrentReading(_buttons, _switches, _axes);
        return _reader.Read(_buttons, _directionIndex >= 0 ? _switches[_directionIndex] : GameControllerSwitchPosition.Center, _axes);
    }
}
