namespace Paradise.Windowing;

/// <summary>Every gamepad button a window backend can report, named by POSITION rather than
/// label — an Xbox A and a DualShock cross are the same button. Triggers appear as buttons
/// (the digital threshold is the backend's); stick clicks are buttons, while stick movement
/// uses <see cref="WindowEventKind.Axis"/> events.</summary>
public enum GamepadButton : byte
{
    South, East, West, North,
    LeftShoulder, RightShoulder,
    LeftTrigger, RightTrigger,
    LeftStick, RightStick,
    DpadUp, DpadDown, DpadLeft, DpadRight,
    Start, Back, Guide,
}
