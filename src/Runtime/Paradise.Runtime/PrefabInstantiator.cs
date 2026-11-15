using System.Numerics;
using System.Text.Json;
using Paradise.Authoring;
using Paradise.Export.Data;

namespace Paradise.Runtime;

/// <summary>One entity of an instantiated prefab, as the game's world receives it.</summary>
/// <param name="Index">The entity's position in the document, which is also the order entities are created in.</param>
/// <param name="Id">Its <c>meta</c> identity, or <see cref="Guid.Empty"/> when it has none.</param>
/// <param name="Name">Its <c>meta</c> display name.</param>
/// <param name="Parent">The parent's <see cref="Index"/>, or -1 for a root.</param>
/// <param name="Local">The <c>transform</c> component as a System.Numerics row-vector matrix (scale, then
/// rotation, then translation), the convention <c>PbrInstance.Model</c> uses.</param>
/// <param name="World"><paramref name="Local"/> composed with every ancestor and the instantiation root.</param>
/// <param name="Components">The game's authored components, materialized by its registry, in document order.</param>
/// <param name="Unresolved">Payloads the registry could not read.</param>
public readonly record struct PrefabEntity(
    int Index,
    Guid Id,
    string? Name,
    int Parent,
    Matrix4x4 Local,
    Matrix4x4 World,
    IReadOnlyList<object> Components,
    IReadOnlyList<AuthoredComponentData> Unresolved);

/// <summary>Instantiates a cooked prefab into whatever world a game keeps.</summary>
/// <remarks>The engine cannot name a game's ECS components (each game's world is generated from its own
/// declarations), so instantiation stops at plain data: this resolves the well-known <c>meta</c> and
/// <c>transform</c> components into identity, hierarchy and world placement, materializes the rest with the
/// game's generated <see cref="IAuthoredComponentRegistry"/>, and hands each entity to the game, which spawns
/// it and adds its components.</remarks>
public static class PrefabInstantiator
{
    /// <summary>Calls <paramref name="create"/> once per entity, in document order.</summary>
    /// <param name="root">Where the prefab is placed (row-vector convention).</param>
    /// <param name="problems">Collects malformed metadata: duplicate identities, unknown or cyclic parents
    /// (such entities are treated as roots). Null ignores them.</param>
    /// <returns>The number of entities created.</returns>
    public static int Instantiate(
        PrefabData prefab,
        Matrix4x4 root,
        Action<PrefabEntity> create,
        IAuthoredComponentRegistry? registry = null,
        ICollection<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        ArgumentNullException.ThrowIfNull(create);

        var count = prefab.Entities.Count;
        var ids = new Guid[count];
        var names = new string?[count];
        var parentIds = new Guid?[count];
        var locals = new Matrix4x4[count];
        var others = new List<AuthoredComponentData>[count];
        var byId = new Dictionary<Guid, int>(count);

        for (var i = 0; i < count; i++)
        {
            locals[i] = Matrix4x4.Identity;
            others[i] = [];
            foreach (var component in prefab.Entities[i])
            {
                if (Is(component, WellKnownEntityComponents.MetaId, WellKnownEntityComponents.MetaType))
                    ReadMeta(component.Data, out ids[i], out names[i], out parentIds[i]);
                else if (Is(component, WellKnownEntityComponents.TransformId, WellKnownEntityComponents.TransformType))
                    locals[i] = ReadTransform(component.Data);
                else
                    others[i].Add(component);
            }

            if (ids[i] == Guid.Empty) continue;
            if (!byId.TryAdd(ids[i], i))
                problems?.Add($"Entity {i} repeats identity {ids[i]} of entity {byId[ids[i]]}; children name the first.");
        }

        var parents = new int[count];
        for (var i = 0; i < count; i++)
        {
            parents[i] = -1;
            if (parentIds[i] is not { } parent) continue;
            if (byId.TryGetValue(parent, out var index) && index != i) parents[i] = index;
            else problems?.Add($"Entity {i} names parent {parent}, which is not in the prefab; it is placed as a root.");
        }

        // World placement by walking up, with cycle detection: 0 unvisited, 1 on the walk, 2 done.
        var worlds = new Matrix4x4[count];
        var visit = new byte[count];
        for (var i = 0; i < count; i++) Resolve(i);

        for (var i = 0; i < count; i++)
        {
            var unresolved = new List<AuthoredComponentData>();
            var components = AuthoredComponentRouter.Materialize(others[i], registry, unresolved);
            create(new PrefabEntity(i, ids[i], names[i], parents[i], locals[i], worlds[i], components, unresolved));
        }

        return count;

        void Resolve(int index)
        {
            if (visit[index] == 2) return;
            if (visit[index] == 1)
            {
                problems?.Add($"Entity {index} is its own ancestor; it is placed as a root.");
                parents[index] = -1;
                worlds[index] = locals[index] * root;
                visit[index] = 2;
                return;
            }

            visit[index] = 1;
            var parent = parents[index];
            if (parent >= 0) Resolve(parent);
            if (visit[index] == 2) return; // the cycle was broken here while walking up
            worlds[index] = parents[index] >= 0 ? locals[index] * worlds[parents[index]] : locals[index] * root;
            visit[index] = 2;
        }
    }

    private static bool Is(AuthoredComponentData component, Guid id, string type) =>
        component.Id == id || (component.Id == Guid.Empty && string.Equals(component.Type, type, StringComparison.Ordinal));

    private static void ReadMeta(JsonElement data, out Guid id, out string? name, out Guid? parent)
    {
        id = Guid.Empty;
        name = null;
        parent = null;
        if (data.ValueKind != JsonValueKind.Object) return;
        if (data.TryGetProperty(WellKnownEntityComponents.Guid, out var idValue) && idValue.ValueKind == JsonValueKind.String
            && Guid.TryParse(idValue.GetString(), out var parsed))
            id = parsed;
        if (data.TryGetProperty(WellKnownEntityComponents.Name, out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            name = nameValue.GetString();
        if (data.TryGetProperty(WellKnownEntityComponents.Parent, out var parentValue) && parentValue.ValueKind == JsonValueKind.String
            && Guid.TryParse(parentValue.GetString(), out var parentId))
            parent = parentId;
    }

    private static Matrix4x4 ReadTransform(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return Matrix4x4.Identity;
        Span<float> position = [0f, 0f, 0f];
        Span<float> rotation = [0f, 0f, 0f, 1f];
        Span<float> scale = [1f, 1f, 1f];
        Read(data, WellKnownEntityComponents.Position, position);
        Read(data, WellKnownEntityComponents.Rotation, rotation);
        Read(data, WellKnownEntityComponents.Scale, scale);
        var orientation = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
        orientation = orientation.LengthSquared() > 1e-12f ? Quaternion.Normalize(orientation) : Quaternion.Identity;
        return Matrix4x4.CreateScale(scale[0], scale[1], scale[2])
            * Matrix4x4.CreateFromQuaternion(orientation)
            * Matrix4x4.CreateTranslation(position[0], position[1], position[2]);
    }

    private static void Read(JsonElement data, string property, Span<float> values)
    {
        if (!data.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return;
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (i == values.Length) break;
            if (item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var value)) values[i] = value;
            i++;
        }
    }
}
