using System.Numerics;

namespace Paradise.Assets.Documents;

/// <summary>
/// The authoring scene document — the committed source of truth a <c>*.scene.toml</c> holds.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately NOT the export contract. The contract JSON is a bake: world-space
/// matrices, references resolved to values, no identities. The authoring document is what the
/// bake is computed <i>from</i>, so it keeps exactly what baking destroys:
/// </para>
/// <list type="bullet">
/// <item><b>Entity GUIDs</b> — promoted from .blend-only bookkeeping
/// (<c>obj.paradise.entity_guid</c>) to durable identity. Same format the addon already mints:
/// hyphenated lowercase uuid4.</item>
/// <item><b>Local transforms and parents</b> — TRS plus an optional parent GUID; the build
/// flattens to the contract's single world matrix.</item>
/// <item><b>References as references</b> — an <c>[AuthoredByHost]</c> field stays a target
/// GUID or an assets-relative path; the build bakes it to a value, preserving the "authored as
/// a REFERENCE, exported as a VALUE" asymmetry.</item>
/// </list>
/// <para>
/// Component entries mirror the export contract's <c>{Id, Type, Data}</c> triple, and their
/// order is kept — the runtime applies components in document order, so order is data.
/// Payloads are open tables (<see cref="CanonicalTomlTable"/>): their schema belongs to the
/// game's authoring schema dump, not to this type.
/// </para>
/// </remarks>
public sealed class SceneDocument
{
    /// <summary>The only <c>schema_version</c> this build reads or writes.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The document's objects, in document order.</summary>
    public List<SceneObject> Objects { get; } = [];
}

/// <summary>One authored object: identity, placement, and its component entries.</summary>
public sealed class SceneObject
{
    /// <summary>Creates an object with the two members every object must have.</summary>
    /// <param name="guid">Durable identity, unique per document.</param>
    /// <param name="name">Display name. Diagnostics and entity-reference bake input; not unique.</param>
    public SceneObject(Guid guid, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Guid = guid;
        Name = name;
    }

    /// <summary>Durable identity, unique per document.</summary>
    public Guid Guid { get; }

    /// <summary>Display name; not unique — identity is <see cref="Guid"/>.</summary>
    public string Name { get; }

    /// <summary>The parent object's GUID, or <see langword="null"/> for a root object.</summary>
    public Guid? Parent { get; set; }

    /// <summary>Local TRS relative to <see cref="Parent"/> (or the world when root).</summary>
    public SceneTransform Transform { get; set; } = SceneTransform.Identity;

    /// <summary>Component entries in document order. Order is data — the bake preserves it.</summary>
    public List<SceneComponent> Components { get; } = [];
}

/// <summary>
/// A local TRS, in engine convention: right-handed Y-up, meters, quaternion as
/// <c>[x, y, z, w]</c> — the same value conventions as the export contract's flat arrays.
/// </summary>
/// <param name="Position">Local translation.</param>
/// <param name="Rotation">Local rotation.</param>
/// <param name="Scale">Local scale.</param>
public readonly record struct SceneTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    /// <summary>No translation, no rotation, unit scale — the value an omitted transform means.</summary>
    public static SceneTransform Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

/// <summary>
/// One component entry: the contract's <c>{Id, Type, Data}</c> triple in authoring form.
/// </summary>
public sealed class SceneComponent
{
    /// <summary>Creates a component entry.</summary>
    /// <param name="id">The component's <c>[Guid]</c> id — the primary key, as in the contract.</param>
    /// <param name="type">Fully-qualified CLR name; a readable fallback key, optional on the wire.</param>
    /// <param name="data">The authored payload; an absent table means an empty one.</param>
    public SceneComponent(Guid id, string? type = null, CanonicalTomlTable? data = null)
    {
        Id = id;
        Type = type;
        Data = data ?? new CanonicalTomlTable();
    }

    /// <summary>The component's <c>[Guid]</c> id.</summary>
    public Guid Id { get; }

    /// <summary>Fully-qualified CLR name, the fallback key. Optional.</summary>
    public string? Type { get; }

    /// <summary>The authored payload, an open table owned by the game's authoring schema.</summary>
    public CanonicalTomlTable Data { get; }
}

/// <summary>A scene document could not be read, parsed, or validated.</summary>
public sealed class SceneDocumentException : Exception
{
    /// <summary>Creates an exception describing a problem with <paramref name="sourceName"/>.</summary>
    /// <param name="sourceName">The document path, or another name for the source text.</param>
    /// <param name="problem">The problem, phrased to follow the source name.</param>
    /// <param name="innerException">The underlying failure, when there was one.</param>
    public SceneDocumentException(string sourceName, string problem, Exception? innerException = null)
        : base($"Scene document '{sourceName}' {problem}.", innerException)
    {
        SourceName = sourceName;
    }

    /// <summary>The document this failure is about.</summary>
    public string SourceName { get; }
}
