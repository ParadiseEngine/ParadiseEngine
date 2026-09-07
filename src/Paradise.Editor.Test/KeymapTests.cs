using Paradise.Editor.Core.Input;
using Paradise.Editor.Core.Shell;
using Paradise.Windowing;
using Zio;
using Zio.FileSystems;

namespace Paradise.Editor.Test;

/// <summary>Chords, layering and the file they come from.</summary>
public class KeymapTests
{
    private static readonly UPath Path = "/keymap.toml";

    [Test]
    [Arguments("Ctrl+Z", KeyboardKey.Z, ChordModifiers.Control)]
    [Arguments("ctrl+shift+p", KeyboardKey.P, ChordModifiers.Control | ChordModifiers.Shift)]
    [Arguments("Control + Alt + Delete", KeyboardKey.Delete, ChordModifiers.Control | ChordModifiers.Alt)]
    [Arguments("F5", KeyboardKey.F5, ChordModifiers.None)]
    [Arguments("Option+A", KeyboardKey.A, ChordModifiers.Alt)]
    public async Task a_chord_parses_however_it_is_spelled(string text, KeyboardKey key, ChordModifiers modifiers)
    {
        await Assert.That(Chord.TryParse(text, out var chord)).IsTrue();
        await Assert.That(chord).IsEqualTo(new Chord(key, modifiers));
    }

    [Test]
    [Arguments("")]
    [Arguments("Ctrl")]        // modifiers only
    [Arguments("Ctrl+A+B")]    // two keys is a typo, not a two-key chord
    [Arguments("Ctrl+Nope")]
    [Arguments("Ctrl+")]
    public async Task a_chord_that_is_not_one_is_refused(string text) =>
        await Assert.That(Chord.TryParse(text, out _)).IsFalse();

    // The canonical spelling is what gets written back, so a user who typed "shift+ctrl+p" sees a
    // stable file rather than their own spelling fighting the editor's on every save.
    [Test]
    public async Task the_canonical_spelling_is_stable()
    {
        Chord.TryParse("shift+ctrl+p", out var chord);
        await Assert.That(chord.ToString()).IsEqualTo("Ctrl+Shift+P");
        Chord.TryParse(chord.ToString(), out var again);
        await Assert.That(again).IsEqualTo(chord);
    }

    [Test]
    public async Task a_later_layer_wins_and_the_displacement_is_reported()
    {
        Chord.TryParse("Ctrl+Z", out var undo);
        var preset = Keymap.Empty.With([new KeyBinding(undo, "editor.undo")], out var first);
        var user = preset.With([new KeyBinding(undo, "game.rewind")], out var second);

        await Assert.That(first).IsEmpty();
        await Assert.That(second.Count).IsEqualTo(1);
        await Assert.That(second[0].Displaced).IsEqualTo("editor.undo");
        await Assert.That(second[0].Winner).IsEqualTo("game.rewind");
        await Assert.That(user.Resolve(undo)).IsEqualTo("game.rewind");
    }

    // A panel taking a chord over is not a conflict with the global binding — it is the point of
    // contexts. Only two bindings on the same chord AND context collide.
    [Test]
    public async Task a_context_binding_beats_the_global_one_without_conflicting()
    {
        Chord.TryParse("Delete", out var delete);
        var keymap = Keymap.Empty
            .With([new KeyBinding(delete, "editor.object.delete")], out _)
            .With([new KeyBinding(delete, "editor.asset.delete", "assets")], out var conflicts);

        await Assert.That(conflicts).IsEmpty();
        await Assert.That(keymap.Resolve(delete)).IsEqualTo("editor.object.delete");
        await Assert.That(keymap.Resolve(delete, "assets")).IsEqualTo("editor.asset.delete");
        await Assert.That(keymap.Resolve(delete, "hierarchy")).IsEqualTo("editor.object.delete");
    }

    [Test]
    public async Task bindings_round_trip_through_the_file()
    {
        var fileSystem = new MemoryFileSystem();
        Chord.TryParse("Ctrl+Shift+P", out var palette);
        Chord.TryParse("Delete", out var delete);
        KeyBinding[] written = [new(palette, "editor.palette.open"), new(delete, "editor.asset.delete", "assets")];

        KeymapDocument.Write(fileSystem, Path, written);
        var read = KeymapDocument.Read(fileSystem, Path, out var problems);

        await Assert.That(problems).IsEmpty();
        await Assert.That(read).IsEquivalentTo(written);
    }

    [Test]
    public async Task a_missing_file_is_not_an_error()
    {
        var read = KeymapDocument.Read(new MemoryFileSystem(), Path, out var problems);
        await Assert.That(read).IsEmpty();
        await Assert.That(problems).IsEmpty();
    }

    // Hand-editable by design, so one bad line costs one binding. An editor that refused to start
    // over a typo in a keymap would be worse than one with a key that does nothing.
    [Test]
    public async Task a_malformed_binding_is_skipped_and_the_rest_still_load()
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.WriteAllText(Path, """
            Version = 1

            [[Binding]]
            Chord = "Ctrl+Nope"
            Operator = "editor.broken"

            [[Binding]]
            Chord = "Ctrl+S"
            Operator = "editor.scene.save"
            """);

        var read = KeymapDocument.Read(fileSystem, Path, out var problems);

        await Assert.That(read.Count).IsEqualTo(1);
        await Assert.That(read[0].OperatorId).IsEqualTo("editor.scene.save");
        await Assert.That(problems.Single()).Contains("Ctrl+Nope");
    }

    [Test]
    public async Task a_file_from_a_newer_editor_is_ignored_rather_than_half_read()
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.WriteAllText(Path, "Version = 99\n\n[[Binding]]\nChord = \"Ctrl+S\"\nOperator = \"x\"\n");

        var read = KeymapDocument.Read(fileSystem, Path, out var problems);

        await Assert.That(read).IsEmpty();
        await Assert.That(problems.Single()).Contains("99");
    }
}
