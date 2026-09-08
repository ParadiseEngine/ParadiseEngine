namespace Paradise.Windowing;

/// <summary>What a <see cref="WindowEvent"/> carries — the discriminator that says which of its
/// fields mean anything. <see cref="Button"/> is a key or button transition (the original and
/// still the common case); the rest are the analog and textual reports a button cannot
/// express.</summary>
public enum WindowEventKind : byte
{
    /// <summary>A key or button went down or came back up: <see cref="WindowEvent.Code"/> and
    /// <see cref="WindowEvent.Pressed"/>.</summary>
    Button = 0,

    /// <summary>The pointer moved to <see cref="WindowEvent.X"/>, <see cref="WindowEvent.Y"/> — an
    /// absolute position in PIXELS.</summary>
    PointerMove = 1,

    /// <summary>A wheel or trackpad scrolled by <see cref="WindowEvent.X"/>,
    /// <see cref="WindowEvent.Y"/> NOTCHES (+Y is a wheel rotated forward, +X to the right).</summary>
    Scroll = 2,

    /// <summary>One Unicode codepoint was composed: <see cref="WindowEvent.Character"/>. Distinct
    /// from a key transition — a keystroke says which key moved, this says what was typed, and
    /// an IME can produce the second without the first.</summary>
    Text = 3,

    /// <summary>An analog axis settled at <see cref="WindowEvent.X"/>:
    /// <see cref="WindowEvent.Code"/> names the axis. State, not a transition — see the note on
    /// <see cref="WindowEvent"/>.</summary>
    Axis = 4,

    /// <summary>Reports the new window size in pixels through the ordered input stream.</summary>
    /// <remarks>Apply before subsequent pointer events; IWindow.Resized remains available for
    /// consumers that do not drain input.</remarks>
    Resize = 5,
}

/// <summary>Which physical device a <see cref="WindowEvent"/> came from — the discriminator for
/// its <see cref="WindowEvent.Code"/>.</summary>
public enum EventSource : byte
{
    Keyboard = 0,
    Gamepad = 1,
    Mouse = 2,

    /// <summary>A touchscreen. <see cref="WindowEvent.Slot"/> is the FINGER, so two fingers are
    /// two slots of one device — the same "which one of these" the gamepad index uses.</summary>
    Touch = 3,

    /// <summary>Not a device: the window itself, for events that are its own rather than any
    /// input hardware's (<see cref="WindowEventKind.Resize"/>). Named rather than left as a
    /// meaningless zero, so a consumer switching on the device cannot mistake one for a
    /// keystroke.</summary>
    Window = 4,
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

/// <summary>Carries one timestamp-orderable raw window event.</summary>
/// <remarks>Use factories and typed accessors. A single stream preserves move/button/key order;
/// timestamps cannot reconstruct order across separate queues. Buttons report transitions without
/// repeat, axes report changed values without deadzones, and consumers retain the latest value.
/// Pointer coordinates are pixels; axes are normalized to -1..1, or 0..1 for triggers.</remarks>
/// <param name="Kind">Which fields of this event mean anything.</param>
/// <param name="Source">Which device produced it — discriminates <paramref name="Code"/>.</param>
/// <param name="Slot">Which device of its kind, for devices that come in multiples: the gamepad
/// index. Always 0 for keyboard and mouse, which the OS has already merged.</param>
/// <param name="Code">The key, button or axis, per <paramref name="Source"/> and
/// <paramref name="Kind"/>.</param>
/// <param name="Pressed">For <see cref="WindowEventKind.Button"/>: down, or back up.</param>
/// <param name="X">Pointer position or scroll delta or axis value, per <paramref
/// name="Kind"/>.</param>
/// <param name="Y">Pointer position or scroll delta, per <paramref name="Kind"/>.</param>
/// <param name="Character">For <see cref="WindowEventKind.Text"/>: the Unicode codepoint.</param>
public readonly record struct WindowEvent(
    WindowEventKind Kind,
    EventSource Source,
    byte Slot,
    byte Code,
    bool Pressed,
    float X,
    float Y,
    uint Character)
{
    public static WindowEvent Keyboard(KeyboardKey key, bool pressed) =>
        new(WindowEventKind.Button, EventSource.Keyboard, 0, (byte)key, pressed, 0f, 0f, 0u);

    public static WindowEvent Gamepad(GamepadButton button, bool pressed, byte slot = 0) =>
        new(WindowEventKind.Button, EventSource.Gamepad, slot, (byte)button, pressed, 0f, 0f, 0u);

    /// <summary>A pointer button transition, carrying where it happened so a consumer that
    /// missed the preceding move still places the click correctly. Pixels.</summary>
    public static WindowEvent Mouse(PointerButton button, bool pressed, float x, float y) =>
        new(WindowEventKind.Button, EventSource.Mouse, 0, (byte)button, pressed, x, y, 0u);

    /// <summary>An absolute pointer position, in PIXELS.</summary>
    public static WindowEvent PointerMove(float x, float y) =>
        new(WindowEventKind.PointerMove, EventSource.Mouse, 0, 0, false, x, y, 0u);

    /// <summary>A scroll delta in NOTCHES: +Y is a wheel rotated forward, +X to the right. A
    /// precise device (a trackpad) reports fractions.</summary>
    public static WindowEvent Scroll(float deltaX, float deltaY) =>
        new(WindowEventKind.Scroll, EventSource.Mouse, 0, 0, false, deltaX, deltaY, 0u);

    /// <summary>One composed Unicode codepoint.</summary>
    public static WindowEvent Text(uint codepoint) =>
        new(WindowEventKind.Text, EventSource.Keyboard, 0, 0, false, 0f, 0f, codepoint);

    /// <summary>An analog axis's new value — normalized, undeadzoned. See the type's
    /// remarks.</summary>
    public static WindowEvent Axis(GamepadAxis axis, float value, byte slot = 0) =>
        new(WindowEventKind.Axis, EventSource.Gamepad, slot, (byte)axis, false, value, 0f, 0u);

    /// <summary>A finger went down or came up, at a position in PIXELS.
    /// <paramref name="finger"/> identifies it for as long as it stays down.</summary>
    public static WindowEvent Touch(byte finger, bool pressed, float x, float y) =>
        new(WindowEventKind.Button, EventSource.Touch, finger, (byte)PointerButton.Left,
            pressed, x, y, 0u);

    /// <summary>A finger moved, in PIXELS. Reported as a pointer move from
    /// <see cref="EventSource.Touch"/>, so a consumer that only cares WHERE can treat mouse and
    /// touch alike, while one that tracks fingers reads <see cref="Slot"/>.</summary>
    public static WindowEvent TouchMove(byte finger, float x, float y) =>
        new(WindowEventKind.PointerMove, EventSource.Touch, finger, 0, false, x, y, 0u);

    /// <summary>A key going down / coming up, spelled for readability at call sites that only
    /// care about the edge.</summary>
    public static WindowEvent KeyDownOf(KeyboardKey key) => Keyboard(key, pressed: true);

    public static WindowEvent KeyUpOf(KeyboardKey key) => Keyboard(key, pressed: false);

    /// <summary>The window's new size in PIXELS.</summary>
    public static WindowEvent Resize(float width, float height) =>
        new(WindowEventKind.Resize, EventSource.Window, 0, 0, false, width, height, 0u);

    /// <summary>The code as a keyboard key. Meaningful only when <see cref="Source"/> says so.</summary>
    public KeyboardKey KeyboardKey => (KeyboardKey)Code;

    /// <summary>The code as a gamepad button. Meaningful only when <see cref="Source"/> says so.</summary>
    public GamepadButton GamepadButton => (GamepadButton)Code;

    /// <summary>The code as a pointer button. Meaningful only when <see cref="Source"/> says so.</summary>
    public PointerButton PointerButton => (PointerButton)Code;

    /// <summary>The code as a gamepad axis. Meaningful only when <see cref="Kind"/> says so.</summary>
    public GamepadAxis GamepadAxis => (GamepadAxis)Code;

    /// <summary>The axis's value, for <see cref="WindowEventKind.Axis"/> — the same storage as
    /// <see cref="X"/>, named for what it is at the call site.</summary>
    public float AxisValue => X;
}

/// <summary>Pairs an input event with the platform's monotonic pump time.</summary>
/// <remarks>All windows share the same epoch, so late drains preserve event timing and streams
/// remain comparable.</remarks>
public readonly record struct TimedWindowEvent(TimeSpan Timestamp, WindowEvent Event);
