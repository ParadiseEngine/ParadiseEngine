using Paradise.Assets.Documents;
using Paradise.Editor.Core.Shell;
using Zio;

namespace Paradise.Editor.Core.Input;

/// <summary>The keymap file: a shipped preset, and the user's override beside it.</summary>
/// <remarks>
/// <para>
/// TOML through the canonical writer, like every other document this project produces, so a
/// hand-edited keymap and a saved one are the same bytes and a diff shows what a person changed.
/// </para>
/// <para>
/// A malformed binding is REPORTED AND SKIPPED, never fatal. This file is hand-editable by design;
/// a typo in one chord must not cost the user every other binding, and an editor that refuses to
/// start because of it is worse than one with a key that does nothing. The Console shows what was
/// dropped.
/// </para>
/// </remarks>
public static class KeymapDocument
{
    /// <summary>Bumped when the SHAPE changes. A file below this is discarded rather than shimmed:
    /// a keymap is re-derivable from the preset, so migration code would outlive its usefulness.</summary>
    public const int Version = 1;

    private const string VersionKey = "Version";
    private const string BindingKey = "Binding";
    private const string ChordKey = "Chord";
    private const string OperatorKey = "Operator";
    private const string ContextKey = "Context";

    /// <summary>Read the bindings from <paramref name="path"/>, or none when it does not exist.
    /// <paramref name="problems"/> holds one line per binding that could not be read.</summary>
    public static IReadOnlyList<KeyBinding> Read(
        IFileSystem fileSystem, UPath path, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var reported = new List<string>();
        problems = reported;

        if (!fileSystem.FileExists(path)) return [];

        var document = CanonicalTomlReader.Parse(fileSystem.ReadAllText(path), path.FullName);
        if (document.Value(VersionKey) is not long version || version > Version)
        {
            reported.Add($"'{path}': version {document.Value(VersionKey) ?? "(missing)"} is not readable by this editor (expected {Version}); ignoring the file.");
            return [];
        }

        if (document.Value(BindingKey) is not IReadOnlyList<CanonicalTomlTable> entries) return [];

        var bindings = new List<KeyBinding>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Value(ChordKey) is not string chordText || !Chord.TryParse(chordText, out var chord))
            {
                reported.Add($"'{path}': '{entry.Value(ChordKey) ?? "(missing)"}' is not a chord; skipped.");
                continue;
            }
            if (entry.Value(OperatorKey) is not string operatorId || operatorId.Length == 0)
            {
                reported.Add($"'{path}': the binding for '{chord}' names no operator; skipped.");
                continue;
            }
            bindings.Add(new KeyBinding(chord, operatorId, entry.Value(ContextKey) as string));
        }

        return bindings;
    }

    /// <summary>Write <paramref name="bindings"/> to <paramref name="path"/>, creating its
    /// directory.</summary>
    public static void Write(IFileSystem fileSystem, UPath path, IEnumerable<KeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(bindings);

        var entries = new List<CanonicalTomlTable>();
        foreach (var binding in bindings)
        {
            var entry = new CanonicalTomlTable
            {
                { ChordKey, binding.Chord.ToString() },
                { OperatorKey, binding.OperatorId },
            };
            if (binding.Context is { Length: > 0 } context) entry.Add(ContextKey, context);
            entries.Add(entry);
        }

        var document = new CanonicalTomlTable { { VersionKey, Version } };
        if (entries.Count > 0) document.Add(BindingKey, entries);

        var directory = path.GetDirectory();
        if (!directory.IsEmpty && !fileSystem.DirectoryExists(directory)) fileSystem.CreateDirectory(directory);
        fileSystem.WriteAllText(path, CanonicalTomlWriter.WriteString(document));
    }
}
