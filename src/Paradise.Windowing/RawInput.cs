namespace Paradise.Windowing;

/// <summary>Which physical device a <see cref="RawInput"/> came from — the discriminator for
/// its <see cref="RawInput.Code"/>.</summary>
public enum InputDevice : byte
{
    Keyboard = 0,
    Gamepad = 1,
}

/// <summary>One raw input event, verbatim as the window backend reported it: a key went down
/// (<see cref="Pressed"/>) or came back up. Transitions only — backends must not forward
/// auto-repeat, which would read as phantom presses. One event type across devices (a tape is
/// one stream), discriminated by <see cref="Device"/>: construct through
/// <see cref="Keyboard"/> / <see cref="Gamepad"/> and read through the typed accessors rather
/// than touching <see cref="Code"/> directly.</summary>
public readonly record struct RawInput(InputDevice Device, byte Code, bool Pressed)
{
    public static RawInput Keyboard(KeyboardKey key, bool pressed) =>
        new(InputDevice.Keyboard, (byte)key, pressed);

    public static RawInput Gamepad(GamepadButton button, bool pressed) =>
        new(InputDevice.Gamepad, (byte)button, pressed);

    /// <summary>The code as a keyboard key. Meaningful only when <see cref="Device"/> says so.</summary>
    public KeyboardKey KeyboardKey => (KeyboardKey)Code;

    /// <summary>The code as a gamepad button. Meaningful only when <see cref="Device"/> says so.</summary>
    public GamepadButton GamepadButton => (GamepadButton)Code;
}

/// <summary>One raw device transition and when it happened. The timestamp is stamped AT THE
/// PUMP — the closest a host gets to when the key actually moved — on the PLATFORM's
/// monotonic clock (elapsed since the platform came up), one epoch for every window it
/// created, so a consumer draining the queue late still records real timings and two windows'
/// streams are directly comparable.</summary>
public readonly record struct TimedRawInput(TimeSpan Timestamp, RawInput Input);
