using Paradise.Windowing;

namespace Paradise.Editor.Core.Input;

/// <summary>The modifiers a chord may carry.</summary>
/// <remarks>
/// No Meta/Command. Hosts map Cmd onto Control in the <c>WindowEvent</c> stream — the same
/// decision <c>ImGuiUiCore</c> makes when it turns off <c>ConfigMacOSXBehaviors</c> — so a keymap
/// spells one chord that works on every platform, and a user's saved override does not become
/// wrong when they move machines. Adding Meta here would mean every shipped binding needing two
/// spellings and neither being the obvious one.
/// </remarks>
[Flags]
public enum ChordModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>A key plus its modifiers: what a keymap binds to an operator id.</summary>
/// <remarks>A value, not a string, because a chord is compared on every key event and parsed
/// once. The string form is the file format and the label a menu shows; <see cref="ToString"/>
/// produces the canonical spelling, so a keymap written back out is stable regardless of how the
/// user spelled it.</remarks>
public readonly record struct Chord(KeyboardKey Key, ChordModifiers Modifiers = ChordModifiers.None)
{
    /// <summary>Parse <c>Ctrl+Shift+P</c>. Case-insensitive, and <c>Control</c>/<c>Ctrl</c> and
    /// <c>Option</c>/<c>Alt</c> are both accepted; whitespace around the parts is ignored.</summary>
    public static bool TryParse(string? text, out Chord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ChordModifiers.None;
        var key = KeyboardKey.None;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;

            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ChordModifiers.Control; continue;
                case "shift": modifiers |= ChordModifiers.Shift; continue;
                case "alt" or "option": modifiers |= ChordModifiers.Alt; continue;
            }

            // The key is whatever is not a modifier, and there must be exactly one of it: "Ctrl+A+B"
            // is a typo, not a two-key chord, and silently taking the last would bind the wrong key.
            if (key != KeyboardKey.None) return false;
            if (!Enum.TryParse(part, ignoreCase: true, out key) || key == KeyboardKey.None) return false;
        }

        if (key == KeyboardKey.None) return false;
        chord = new Chord(key, modifiers);
        return true;
    }

    /// <summary>The canonical spelling: modifiers in Ctrl, Shift, Alt order, then the key.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ChordModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ChordModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ChordModifiers.Alt)) parts.Add("Alt");
        parts.Add(Key.ToString());
        return string.Join('+', parts);
    }
}
