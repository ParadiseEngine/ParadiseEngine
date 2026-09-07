using System.Collections.Immutable;
using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Editor.Core.Document;
using Zio;

namespace Paradise.Editor.Core.Persistence;

/// <summary>Where the user was in this project last time: the open scene and what was selected.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>.editor/</c>, in the editor's OWN file rather than the pipeline's
/// <c>state.toml</c> — two writers on one file is a corruption waiting for a build to run while
/// the editor is open, and the two have different lifetimes besides.
/// </para>
/// <para>
/// Deleting <c>.editor/</c> loses nothing, which is what makes the version handling simple: a file
/// this editor cannot read is DISCARDED, never shimmed. Migration code for disposable state earns
/// its maintenance forever and buys a user one remembered selection.
/// </para>
/// <para>
/// Never in <c>assets/</c>. What one developer had selected is not something a team shares.
/// </para>
/// </remarks>
public sealed record EditorProjectState(UPath? LastScene, ImmutableList<NodeId> Selection)
{
    public const int Version = 1;

    /// <summary>Files below this are discarded. Raise it when a field's MEANING changes rather
    /// than when one is added — an added field simply reads as absent.</summary>
    public const int MinimumReadableVersion = 1;

    public static string FileName => "editor.toml";

    public static EditorProjectState Empty { get; } = new(null, ImmutableList<NodeId>.Empty);

    /// <summary>Where this file sits for <paramref name="layout"/>, beside the pipeline's state.</summary>
    public static UPath PathFor(AssetProjectLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Editor / FileName;
    }

    /// <summary>Read the state, or <see cref="Empty"/> when it is absent, unreadable, or from a
    /// version this editor does not accept.</summary>
    public static EditorProjectState Read(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!fileSystem.FileExists(path)) return Empty;

        try
        {
            var document = CanonicalTomlReader.Parse(fileSystem.ReadAllText(path), path.FullName);
            if (document.Value("Version") is not long version
                || version < MinimumReadableVersion
                || version > Version)
            {
                return Empty;
            }

            var selection = document.Value("Selection") is IReadOnlyList<object> ids
                ? ids.OfType<string>()
                    .Select(text => DocumentGuid.TryParse(text, out var guid) ? new NodeId(guid) : default)
                    .Where(id => id != default)
                    .ToImmutableList()
                : ImmutableList<NodeId>.Empty;

            var scene = document.Value("LastScene") as string;
            return new EditorProjectState(scene is { Length: > 0 } ? new UPath(scene) : default(UPath?), selection);
        }
        catch (InvalidDataException)
        {
            return Empty;
        }
    }

    public void Write(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var document = new CanonicalTomlTable { { "Version", Version } };
        // A UPath is '/'-separated on every platform, which is exactly how it is written back —
        // no separator translation, here or anywhere else that stores one.
        if (LastScene is { } scene) document.Add("LastScene", scene.FullName);
        if (!Selection.IsEmpty)
        {
            document.Add("Selection", Selection.Select(id => (object)DocumentGuid.Format(id.Value)).ToArray());
        }

        var directory = path.GetDirectory();
        if (!directory.IsEmpty && !fileSystem.DirectoryExists(directory)) fileSystem.CreateDirectory(directory);
        fileSystem.WriteAllText(path, CanonicalTomlWriter.WriteString(document));
    }
}
