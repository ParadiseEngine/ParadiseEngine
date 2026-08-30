using Zio;

namespace Paradise.Assets.Documents;

/// <summary>
/// A prefab: a reusable object, or small tree of objects, that scenes instantiate.
/// </summary>
/// <remarks>
/// <para>
/// Structurally a <see cref="SceneDocument"/> — the same objects, the same components, the same
/// reader and writer. A prefab is not a different KIND of document, it is a document that scenes
/// point at, and keeping them one shape means one mental model and no second serializer to drift.
/// </para>
/// <para>
/// The one rule a prefab adds is <b>exactly one root</b>, inferred from the absence of
/// <c>meta.Parent</c> rather than declared. Inferred so that nothing can disagree with the
/// hierarchy; exactly one because an instance places exactly one thing, and "which of these
/// several is the instance" has no good answer. Every comparable system — Unity prefabs, Godot's
/// PackedScene, Unreal blueprints — lands on the same rule.
/// </para>
/// <para>
/// Guids inside a prefab are <b>prefab-local</b>: unique within the file, and meaningless outside
/// it. Copying a prefab therefore collides with nothing, and an instance's resolved children get
/// freshly minted scene guids rather than the prefab's.
/// </para>
/// </remarks>
public sealed class PrefabDocument
{
    private PrefabDocument(SceneDocument document, SceneObject root)
    {
        Document = document;
        Root = root;
    }

    /// <summary>The objects, in document order.</summary>
    public SceneDocument Document { get; }

    /// <summary>The single object with no parent — what an instance becomes.</summary>
    public SceneObject Root { get; }

    /// <summary>The root's prefab-local identity.</summary>
    public Guid RootGuid => Root.Guid!.Value;

    /// <summary>Reads and validates the prefab at <paramref name="path"/>.</summary>
    /// <exception cref="SceneDocumentException">Unreadable, not TOML, or not a valid prefab.</exception>
    public static PrefabDocument Load(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return Validate(SceneDocumentSerializer.Load(fileSystem, path), path.FullName);
    }

    /// <summary>Validates already-read text. The filesystem-free half of <see cref="Load"/>.</summary>
    /// <exception cref="SceneDocumentException">Not TOML, or not a valid prefab.</exception>
    public static PrefabDocument Parse(string toml, string sourceName)
        => Validate(SceneDocumentSerializer.Parse(toml, sourceName), sourceName);

    /// <summary>Applies the prefab rules to an already-parsed document.</summary>
    /// <exception cref="SceneDocumentException">Zero roots, several roots, or a prefab-only rule broken.</exception>
    public static PrefabDocument Validate(SceneDocument document, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(document);

        Exception Fail(string problem) => new SceneDocumentException(sourceName, problem);

        if (document.Objects.Count == 0) throw Fail("is a prefab with no objects");

        var roots = document.Objects.Where(o => o.Parent is null).ToList();
        if (roots.Count == 0)
        {
            // Every object has a parent, which for a valid document means they form a cycle --
            // and the serializer already refuses those, so this is the "someone deleted the root"
            // case rather than anything exotic.
            throw Fail("is a prefab with no root object (every object declares a parent)");
        }

        if (roots.Count > 1)
        {
            var names = string.Join(", ", roots.Select(r => r.Name ?? DocumentGuid.Format(r.Guid ?? Guid.Empty)));
            throw Fail(
                $"is a prefab with {roots.Count} root objects ({names}); a prefab has exactly one, " +
                "because an instance places exactly one thing — parent the others beneath it");
        }

        foreach (var candidate in document.Objects)
        {
            if (candidate.Prefab is not null)
            {
                // The format can express it; the resolver cannot yet, and a reference that reads
                // fine and then fails deep in resolution is worse than one refused up front.
                throw Fail("is a prefab that instantiates another prefab, which is not supported yet");
            }

            if (candidate.Target is not null)
            {
                throw Fail("is a prefab containing an override carrier, which only a scene may hold");
            }
        }

        return new PrefabDocument(document, roots[0]);
    }
}
