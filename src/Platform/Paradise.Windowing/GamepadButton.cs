namespace Paradise.Windowing;

/// <summary>Every gamepad button a window backend can report, named by POSITION rather than
/// label — an Xbox A and a DualShock cross are the same button. Triggers appear as buttons
/// (the digital threshold is the backend's); analog sticks are not buttons and will arrive as
/// their own raw event kind when a consumer needs them.</summary>
public enum GamepadButton : byte
{
    South, East, West, North,
    LeftShoulder, RightShoulder,
    LeftTrigger, RightTrigger,
    LeftStick, RightStick,
    DpadUp, DpadDown, DpadLeft, DpadRight,
    Start, Back, Guide,
}
