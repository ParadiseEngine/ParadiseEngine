using System.Collections.Immutable;
using Paradise.Assets.Documents;

namespace Paradise.Editor.Core.Document;

/// <summary>An authored component's ID, type name and raw payload.</summary>
/// <remarks>Treat <see cref="Data"/> as immutable inside a document; edits create a new table.</remarks>
public sealed record SceneComponent(Guid Id, string? Type, CanonicalTomlTable Data);

/// <summary>A scene object's durable identity and ordered components.</summary>
/// <remarks>
/// Name and parent are read from <c>meta</c> to avoid duplicate state.
/// <see cref="Id"/> caches the immutable <c>meta.Guid</c>; create it consistently with <see cref="WithMeta"/>.
/// Other metadata remains in <see cref="Components"/> and survives round trips.
/// </remarks>
public sealed record SceneObject(NodeId Id, ImmutableList<SceneComponent> Components)
{
    /// <summary>An object carrying just a <c>meta</c> component, formatted the way the document
    /// contract spells it.</summary>
    public static SceneObject WithMeta(NodeId id, string? name = null, NodeId? parent = null)
    {
        var data = new CanonicalTomlTable { { WellKnownComponents.Guid, DocumentGuid.Format(id.Value) } };
        if (name is not null) data.Add(WellKnownComponents.Name, name);
        if (parent is { } value) data.Add(WellKnownComponents.Parent, DocumentGuid.Format(value.Value));

        return new SceneObject(id, [new SceneComponent(WellKnownComponents.MetaId, WellKnownComponents.MetaType, data)]);
    }

    public SceneComponent? Meta => Component(WellKnownComponents.MetaId);

    /// <summary>Display name, from <c>meta.Name</c>. Not identity, and not unique.</summary>
    public string? Name => Meta?.Data.Value(WellKnownComponents.Name) as string;

    /// <summary>The parent's identity, from <c>meta.Parent</c>.</summary>
    public NodeId? Parent =>
        Meta?.Data.Value(WellKnownComponents.Parent) is string text && DocumentGuid.TryParse(text, out var guid)
            ? new NodeId(guid)
            : null;

    public SceneComponent? Component(Guid id)
    {
        foreach (var candidate in Components)
        {
            if (candidate.Id == id) return candidate;
        }
        return null;
    }

    public SceneObject WithName(string? name) => WithMetaField(WellKnownComponents.Name, name);

    public SceneObject WithParent(NodeId? parent) =>
        WithMetaField(WellKnownComponents.Parent, parent is { } value ? DocumentGuid.Format(value.Value) : null);

    // Rebuild the frozen table in key order so editing a value does not reorder the file.
    private SceneObject WithMetaField(string key, object? value)
    {
        var index = Components.FindIndex(component => component.Id == WellKnownComponents.MetaId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Object '{Id}' has no meta component to write '{key}' into.");
        }

        var meta = Components[index];
        var rebuilt = new CanonicalTomlTable();
        var replaced = false;
        foreach (var (existing, held) in meta.Data)
        {
            if (existing == key)
            {
                replaced = true;
                if (value is not null) rebuilt.Add(key, value);
            }
            else
            {
                rebuilt.Add(existing, held);
            }
        }
        if (!replaced && value is not null) rebuilt.Add(key, value);

        return this with { Components = Components.SetItem(index, meta with { Data = rebuilt }) };
    }
}

/// <summary>An immutable working version of an authored scene.</summary>
/// <remarks>
/// Edits share unchanged objects; undo republishes an earlier version.
/// Reference equality identifies unchanged state. Object order determines runtime entity handles.
/// </remarks>
public sealed record SceneDocument(ImmutableList<SceneObject> Objects)
{
    public static SceneDocument Empty { get; } = new(ImmutableList<SceneObject>.Empty);

    public SceneObject? Find(NodeId id)
    {
        foreach (var candidate in Objects)
        {
            if (candidate.Id == id) return candidate;
        }
        return null;
    }

    public IEnumerable<SceneObject> ChildrenOf(NodeId? parent)
    {
        foreach (var candidate in Objects)
        {
            if (candidate.Parent == parent) yield return candidate;
        }
    }

    public SceneDocument Replace(SceneObject updated)
    {
        var index = Objects.FindIndex(candidate => candidate.Id == updated.Id);
        if (index < 0) throw new KeyNotFoundException(updated.Id.ToString());
        return this with { Objects = Objects.SetItem(index, updated) };
    }
}
