using System.Numerics;

namespace Paradise.Ui;

public enum UiEventKind
{
    PointerMove,
    PointerDown,
    PointerUp,
    Resize,
    Scroll,
    KeyDown,
    KeyUp,
    Text,
}

public enum UiPointerButton
{
    Left,
    Right,
    Middle,
}

/// <summary>Engine-neutral key identity for UI text editing and shortcuts. Printable input
/// arrives as <see cref="UiEventKind.Text"/> codepoints; key events carry only the editing /
/// navigation / modifier keys a text field needs, so hosts map a small fixed set.</summary>
public enum UiKey
{
    None,
    Enter,
    Escape,
    Backspace,
    Delete,
    Tab,
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    Ctrl,
    Shift,
    A,
    C,
    D,
    S,
    V,
    W,
    X,
    Y,
    Z,
}

/// <summary>One UI input event, produced on the platform/render thread (SDL, Godot, …) and
/// consumed on the SIMULATION thread by <see cref="IUiInput"/>. Pointer coordinates are in
/// UI pixels (already DPI-scaled by the producer). Pointer-down events may carry a world-space
/// pick ray so game logic can act on clicks the UI did not consume without needing camera
/// state on the sim thread. Scroll events reuse X/Y as the wheel delta; text events carry one
/// Unicode codepoint per event.</summary>
public readonly record struct UiEvent(
    UiEventKind Kind,
    float X,
    float Y,
    UiPointerButton Button,
    Vector3 WorldRayOrigin,
    Vector3 WorldRayDirection,
    bool HasWorldRay,
    UiKey Key = UiKey.None,
    uint Character = 0)
{
    public static UiEvent PointerMove(float x, float y) =>
        new(UiEventKind.PointerMove, x, y, UiPointerButton.Left, default, default, false);

    public static UiEvent PointerDown(float x, float y, UiPointerButton button, Vector3 rayOrigin, Vector3 rayDirection) =>
        new(UiEventKind.PointerDown, x, y, button, rayOrigin, rayDirection, true);

    public static UiEvent PointerUp(float x, float y, UiPointerButton button) =>
        new(UiEventKind.PointerUp, x, y, button, default, default, false);

    public static UiEvent Resize(float width, float height) =>
        new(UiEventKind.Resize, width, height, UiPointerButton.Left, default, default, false);

    public static UiEvent Scroll(float deltaX, float deltaY) =>
        new(UiEventKind.Scroll, deltaX, deltaY, UiPointerButton.Left, default, default, false);

    public static UiEvent KeyDown(UiKey key) =>
        new(UiEventKind.KeyDown, 0f, 0f, UiPointerButton.Left, default, default, false, key);

    public static UiEvent KeyUp(UiKey key) =>
        new(UiEventKind.KeyUp, 0f, 0f, UiPointerButton.Left, default, default, false, key);

    public static UiEvent Text(uint character) =>
        new(UiEventKind.Text, 0f, 0f, UiPointerButton.Left, default, default, false, UiKey.None, character);
}
