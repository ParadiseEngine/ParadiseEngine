namespace Paradise.Windowing;

/// <summary>Every keyboard key a window backend can report. Raw identity, not meaning — full
/// fidelity is free here because raw keys are only TRANSPORTED (as <see cref="WindowEvent"/>
/// events); what a key does is the consuming game's binding to decide, and whatever held
/// state a game keeps is its own, sized to its own action vocabulary.</summary>
public enum KeyboardKey : byte
{
    /// <summary>No key. Zero so that a defaulted field means "nothing" rather than [A] —
    /// consumers that carry a key alongside other data (<c>WindowEvent</c>) rely on it.</summary>
    None = 0,

    // Letters.
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    // Digit row.
    Digit0, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9,

    // Function row.
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    // Arrows.
    Up, Down, Left, Right,

    // Whitespace and editing.
    Space, Enter, Escape, Tab, Backspace, Delete, Insert,
    Home, End, PageUp, PageDown,

    // Modifiers, sided — a binding that cares can tell them apart, one that does not maps
    // both onto the same action.
    LeftShift, RightShift, LeftControl, RightControl,
    LeftAlt, RightAlt, LeftMeta, RightMeta,

    // Punctuation, by US-layout position (the usual scancode convention).
    Minus, Equals, LeftBracket, RightBracket, Backslash,
    Semicolon, Apostrophe, Grave, Comma, Period, Slash,

    // Numpad.
    Numpad0, Numpad1, Numpad2, Numpad3, Numpad4,
    Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    NumpadDivide, NumpadMultiply, NumpadMinus, NumpadPlus,
    NumpadEnter, NumpadPeriod, NumLock,

    // Odds and ends.
    CapsLock, PrintScreen, ScrollLock, Pause,
}
