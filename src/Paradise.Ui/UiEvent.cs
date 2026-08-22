using System.Numerics;
using Paradise.Windowing;

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
    PointerButton Button,
    Vector3 WorldRayOrigin,
    Vector3 WorldRayDirection,
    bool HasWorldRay,
    KeyboardKey Key = KeyboardKey.None,
    uint Character = 0)
{
    public static UiEvent PointerMove(float x, float y) =>
        new(UiEventKind.PointerMove, x, y, PointerButton.Left, default, default, false);

    public static UiEvent PointerDown(float x, float y, PointerButton button, Vector3 rayOrigin, Vector3 rayDirection) =>
        new(UiEventKind.PointerDown, x, y, button, rayOrigin, rayDirection, true);

    public static UiEvent PointerUp(float x, float y, PointerButton button) =>
        new(UiEventKind.PointerUp, x, y, button, default, default, false);

    public static UiEvent Resize(float width, float height) =>
        new(UiEventKind.Resize, width, height, PointerButton.Left, default, default, false);

    public static UiEvent Scroll(float deltaX, float deltaY) =>
        new(UiEventKind.Scroll, deltaX, deltaY, PointerButton.Left, default, default, false);

    public static UiEvent KeyDown(KeyboardKey key) =>
        new(UiEventKind.KeyDown, 0f, 0f, PointerButton.Left, default, default, false, key);

    public static UiEvent KeyUp(KeyboardKey key) =>
        new(UiEventKind.KeyUp, 0f, 0f, PointerButton.Left, default, default, false, key);

    public static UiEvent Text(uint character) =>
        new(UiEventKind.Text, 0f, 0f, PointerButton.Left, default, default, false,
            KeyboardKey.None, character);
}
