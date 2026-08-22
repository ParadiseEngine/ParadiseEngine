namespace Paradise.Windowing;

/// <summary>What a <see cref="RawInput"/> carries — the discriminator that says which of its
/// fields mean anything. <see cref="Button"/> is a key or button transition (the original and
/// still the common case); the rest are the analog and textual reports a button cannot
/// express.</summary>
public enum RawInputKind : byte
{
    /// <summary>A key or button went down or came back up: <see cref="RawInput.Code"/> and
    /// <see cref="RawInput.Pressed"/>.</summary>
    Button = 0,

    /// <summary>The pointer moved to <see cref="RawInput.X"/>, <see cref="RawInput.Y"/> — an
    /// absolute position in PIXELS.</summary>
    PointerMove = 1,

    /// <summary>A wheel or trackpad scrolled by <see cref="RawInput.X"/>,
    /// <see cref="RawInput.Y"/> NOTCHES (+Y is a wheel rotated forward, +X to the right).</summary>
    Scroll = 2,

    /// <summary>One Unicode codepoint was composed: <see cref="RawInput.Character"/>. Distinct
    /// from a key transition — a keystroke says which key moved, this says what was typed, and
    /// an IME can produce the second without the first.</summary>
    Text = 3,

    /// <summary>An analog axis settled at <see cref="RawInput.X"/>:
    /// <see cref="RawInput.Code"/> names the axis. State, not a transition — see the note on
    /// <see cref="RawInput"/>.</summary>
    Axis = 4,
}

/// <summary>Which physical device a <see cref="RawInput"/> came from — the discriminator for
/// its <see cref="RawInput.Code"/>.</summary>
public enum InputDevice : byte
{
    Keyboard = 0,
    Gamepad = 1,
    Mouse = 2,
}

/// <summary>Every pointer button a window backend can report. Named <c>PointerButton</c> rather
/// than <c>MouseButton</c> because hosts consuming this sit next to UI toolkits that spell the
/// latter differently (Noesis, ImGui), and an ambiguous using-directive in a host is a worse
/// tax than a slightly longer name here.</summary>
public enum PointerButton : byte
{
    Left, Right, Middle, X1, X2,
}

/// <summary>Every analog axis a gamepad reports, named by POSITION like
/// <see cref="GamepadButton"/>. Sticks range -1..1 with +Y DOWN (the convention every backend
/// reports and every consumer therefore has to know about); triggers range 0..1.</summary>
public enum GamepadAxis : byte
{
    LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger,
}

/// <summary>
/// One raw input event, verbatim as the window backend reported it. One event type across
/// devices and kinds (a tape is one stream) — <see cref="Kind"/> says which fields mean
/// anything, <see cref="Device"/> discriminates <see cref="Code"/>. Construct through the
/// factories and read through the typed accessors rather than touching <see cref="Code"/>
/// directly.
///
/// <b>Why one widened struct rather than a stream per kind:</b> ORDER is load-bearing. A
/// pointer move must precede the button-down that hit-tests against where it left the pointer,
/// and a click must be ordered against a keystroke. Parallel queues cannot express that, and
/// no consumer can reconstruct it from timestamps alone.
///
/// <b>Transitions, except where an axis makes that meaningless.</b> Buttons report edges only —
/// backends must not forward auto-repeat, which would read as phantom presses. An axis has no
/// edges, so <see cref="RawInputKind.Axis"/> reports the value each time the backend observes
/// it change: a consumer holds the last value and a step with no axis event means "still
/// there", never "centred".
///
/// <b>Units are the backend's problem, meaning is the consumer's.</b> Pointer coordinates are
/// in PIXELS, matching <see cref="IWindow.Width"/>/<see cref="IWindow.Height"/> and the surface
/// a renderer draws into — a backend on a scaled display converts before it reports here.
/// Axis values are normalized to -1..1 (0..1 for triggers) and carry NO DEADZONE: a deadzone is
/// calibration policy that belongs with the bindings, the same reason this reports scancodes
/// rather than actions.
/// </summary>
/// <param name="Kind">Which fields of this event mean anything.</param>
/// <param name="Device">Which device produced it — discriminates <paramref name="Code"/>.</param>
/// <param name="Slot">Which device of its kind, for devices that come in multiples: the gamepad
/// index. Always 0 for keyboard and mouse, which the OS has already merged.</param>
/// <param name="Code">The key, button or axis, per <paramref name="Device"/> and
/// <paramref name="Kind"/>.</param>
/// <param name="Pressed">For <see cref="RawInputKind.Button"/>: down, or back up.</param>
/// <param name="X">Pointer position or scroll delta or axis value, per <paramref name="Kind"/>.</param>
/// <param name="Y">Pointer position or scroll delta, per <paramref name="Kind"/>.</param>
/// <param name="Character">For <see cref="RawInputKind.Text"/>: the Unicode codepoint.</param>
public readonly record struct RawInput(
    RawInputKind Kind,
    InputDevice Device,
    byte Slot,
    byte Code,
    bool Pressed,
    float X,
    float Y,
    uint Character)
{
    public static RawInput Keyboard(KeyboardKey key, bool pressed) =>
        new(RawInputKind.Button, InputDevice.Keyboard, 0, (byte)key, pressed, 0f, 0f, 0u);

    public static RawInput Gamepad(GamepadButton button, bool pressed, byte slot = 0) =>
        new(RawInputKind.Button, InputDevice.Gamepad, slot, (byte)button, pressed, 0f, 0f, 0u);

    /// <summary>A pointer button transition, carrying where it happened so a consumer that
    /// missed the preceding move still places the click correctly. Pixels.</summary>
    public static RawInput Mouse(PointerButton button, bool pressed, float x, float y) =>
        new(RawInputKind.Button, InputDevice.Mouse, 0, (byte)button, pressed, x, y, 0u);

    /// <summary>An absolute pointer position, in PIXELS.</summary>
    public static RawInput PointerMove(float x, float y) =>
        new(RawInputKind.PointerMove, InputDevice.Mouse, 0, 0, false, x, y, 0u);

    /// <summary>A scroll delta in NOTCHES: +Y is a wheel rotated forward, +X to the right. A
    /// precise device (a trackpad) reports fractions.</summary>
    public static RawInput Scroll(float deltaX, float deltaY) =>
        new(RawInputKind.Scroll, InputDevice.Mouse, 0, 0, false, deltaX, deltaY, 0u);

    /// <summary>One composed Unicode codepoint.</summary>
    public static RawInput Text(uint codepoint) =>
        new(RawInputKind.Text, InputDevice.Keyboard, 0, 0, false, 0f, 0f, codepoint);

    /// <summary>An analog axis's new value — normalized, undeadzoned. See the type's
    /// remarks.</summary>
    public static RawInput Axis(GamepadAxis axis, float value, byte slot = 0) =>
        new(RawInputKind.Axis, InputDevice.Gamepad, slot, (byte)axis, false, value, 0f, 0u);

    /// <summary>The code as a keyboard key. Meaningful only when <see cref="Device"/> says so.</summary>
    public KeyboardKey KeyboardKey => (KeyboardKey)Code;

    /// <summary>The code as a gamepad button. Meaningful only when <see cref="Device"/> says so.</summary>
    public GamepadButton GamepadButton => (GamepadButton)Code;

    /// <summary>The code as a pointer button. Meaningful only when <see cref="Device"/> says so.</summary>
    public PointerButton PointerButton => (PointerButton)Code;

    /// <summary>The code as a gamepad axis. Meaningful only when <see cref="Kind"/> says so.</summary>
    public GamepadAxis GamepadAxis => (GamepadAxis)Code;

    /// <summary>The axis's value, for <see cref="RawInputKind.Axis"/> — the same storage as
    /// <see cref="X"/>, named for what it is at the call site.</summary>
    public float AxisValue => X;
}

/// <summary>One raw device event and when it happened. The timestamp is stamped AT THE
/// PUMP — the closest a host gets to when the input actually happened — on the PLATFORM's
/// monotonic clock (elapsed since the platform came up), one epoch for every window it
/// created, so a consumer draining the queue late still records real timings and two windows'
/// streams are directly comparable.</summary>
public readonly record struct TimedRawInput(TimeSpan Timestamp, RawInput Input);
