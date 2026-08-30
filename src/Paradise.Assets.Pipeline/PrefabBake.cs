using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Authoring;
using Paradise.Export.Data;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Turns an authoring document into the export contract the runtime loads.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a document transform, not a translation.</b> Contract v5 reduced a level to its
/// entities and an entity to its authored components, which is the same shape the authoring
/// document already has — so almost every component crosses over untouched, carrying the id and
/// type it was authored with. Only the two the authoring side treats as structure need mapping.
/// </para>
/// <para>
/// What the bake actually destroys is what the authoring document exists to keep:
/// </para>
/// <list type="bullet">
///   <item>instances become plain objects — resolved, recursively, so nothing downstream knows
///     prefabs exist;</item>
///   <item>local transforms and the parent chain become one world matrix per object, because
///     nothing reads a hierarchy at load;</item>
///   <item>references become values — an <see cref="AssetReference"/>'s guid is an authoring
///     concern, and the runtime resolves a path.</item>
/// </list>
/// </remarks>
public static class PrefabBake
{
    /// <summary>The engine components the authoring side keeps as <c>meta</c> and <c>transform</c>.</summary>
    private static readonly Guid s_nameId = typeof(NameComponentData).GUID;
    private static readonly Guid s_transformId = typeof(TransformComponentData).GUID;

    /// <summary>Bakes <paramref name="document"/>, appending anything that went wrong to <paramref name="errors"/>.</summary>
    /// <param name="document">The authoring document, instances and all.</param>
    /// <param name="prefabs">Resolves a prefab reference to its document, or returns null.</param>
    /// <param name="documentExtension">What a reference to an authored document becomes, e.g. <c>.json</c>.</param>
    /// <param name="errors">Collects resolution failures; the result is still returned.</param>
    public static LevelData Bake(
        PrefabDocument document,
        Func<AssetReference, PrefabDocument?> prefabs,
        string documentExtension,
        List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(errors);

        var resolved = PrefabResolver.Resolve(document, prefabs);
        foreach (var error in resolved.Errors) errors.Add(error.Message);

        var world = WorldMatrices(resolved.Document);
        var level = new LevelData();

        foreach (var entry in resolved.Document.Objects)
        {
            var components = new List<AuthoredComponentData>();
            var placed = false;

            foreach (var component in entry.Components)
            {
                if (component.Id == WellKnownComponents.MetaId)
                {
                    // Identity and the parent link are authoring concerns and simply stop
                    // existing; the name survives because something has to appear in a message.
                    if (entry.Name is { } name)
                    {
                        components.Add(Entry(s_nameId, typeof(NameComponentData).FullName,
                            new JsonObject { ["Value"] = name }));
                    }

                    continue;
                }

                if (component.Id == WellKnownComponents.TransformId)
                {
                    components.Add(Placement(entry, world));
                    placed = true;
                    continue;
                }

                components.Add(new AuthoredComponentData
                {
                    Id = component.Id,
                    Type = component.Type,
                    Data = ToElement(ToNode(component.Data, documentExtension)),
                });
            }

            // "Anything that exists is somewhere" — LevelDocument calls the transform the one
            // component an exporter writes for EVERY object it emits, and says a runtime is
            // entitled to expect it. An authoring document may legitimately omit it (an object
            // that never moves from its parent's origin), so it is supplied here rather than left
            // to whatever a reader does with an entity that has no placement.
            if (!placed) components.Insert(components.Count > 0 ? 1 : 0, Placement(entry, world));

            level.Entities.Add(components);
        }

        return level;
    }

    /// <summary>An object's placement, as the contract's transform component.</summary>
    private static AuthoredComponentData Placement(PrefabObject entry, Dictionary<Guid, Matrix4x4> world)
        => Entry(s_transformId, typeof(TransformComponentData).FullName,
            new JsonObject { ["World"] = Wire(world.GetValueOrDefault(entry.Guid ?? Guid.Empty, Matrix4x4.Identity)) });

    /// <summary>
    /// Each object's world matrix, composed down the parent chain.
    /// </summary>
    /// <remarks>
    /// Memoised and computed on demand rather than in one forward pass: document order puts a
    /// parent before its children today, but nothing in the format REQUIRES it, and a forward pass
    /// that met a child first would silently place it in its parent's old position.
    /// </remarks>
    private static Dictionary<Guid, Matrix4x4> WorldMatrices(PrefabDocument document)
    {
        var byGuid = document.ByGuid();
        var world = new Dictionary<Guid, Matrix4x4>();

        Matrix4x4 Of(Guid guid, int depth)
        {
            if (world.TryGetValue(guid, out var known)) return known;
            if (depth > 256 || !byGuid.TryGetValue(guid, out var entry)) return Matrix4x4.Identity;

            var local = Local(entry);
            var matrix = entry.Parent is { } parent ? local * Of(parent, depth + 1) : local;

            world[guid] = matrix;
            return matrix;
        }

        foreach (var entry in document.Objects)
        {
            if (entry.Guid is { } guid) Of(guid, 0);
        }

        return world;
    }

    /// <summary>An object's local TRS as a matrix, in System.Numerics' row-vector convention.</summary>
    private static Matrix4x4 Local(PrefabObject entry)
    {
        if (entry.Component(WellKnownComponents.TransformId) is not { } transform) return Matrix4x4.Identity;

        var position = Vector(transform.Data.Value(WellKnownComponents.Position), Vector3.Zero);
        var scale = Vector(transform.Data.Value(WellKnownComponents.Scale), Vector3.One);
        var rotation = Quat(transform.Data.Value(WellKnownComponents.Rotation));

        return Matrix4x4.CreateScale(scale)
             * Matrix4x4.CreateFromQuaternion(rotation)
             * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// A world matrix as the contract's sixteen numbers.
    /// </summary>
    /// <remarks>
    /// <b>Transposed, and this is load-bearing.</b> The contract's matrices are COLUMN-VECTOR
    /// (<c>LevelDocument</c> says so), and <c>Matrix4x4Converter</c> writes
    /// <c>M11, M21, M31, M41, …</c> — the transpose of memory order. Composing in System.Numerics
    /// gives a row-vector matrix, so it is transposed here to land the translation at wire indices
    /// 12..14, which is where every <c>World</c> in ShiningPie's committed <c>data/</c> has it.
    /// Skip this and every object in the game moves to the origin with its rotation transposed.
    /// </remarks>
    private static JsonArray Wire(Matrix4x4 world)
    {
        var m = Matrix4x4.Transpose(world);
        return
        [
            m.M11, m.M21, m.M31, m.M41,
            m.M12, m.M22, m.M32, m.M42,
            m.M13, m.M23, m.M33, m.M43,
            m.M14, m.M24, m.M34, m.M44,
        ];
    }

    private static Vector3 Vector(object? value, Vector3 fallback)
    {
        if (value is not IReadOnlyList<object> numbers || numbers.Count != 3) return fallback;
        return new Vector3(Single(numbers[0]), Single(numbers[1]), Single(numbers[2]));
    }

    private static Quaternion Quat(object? value)
    {
        if (value is not IReadOnlyList<object> numbers || numbers.Count != 4) return Quaternion.Identity;
        return new Quaternion(Single(numbers[0]), Single(numbers[1]), Single(numbers[2]), Single(numbers[3]));
    }

    private static float Single(object value) => Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);

    private static AuthoredComponentData Entry(Guid id, string? type, JsonObject data)
        => new() { Id = id, Type = type, Data = ToElement(data) };

    /// <summary>
    /// A node as a detached <see cref="JsonElement"/>.
    /// </summary>
    /// <remarks>
    /// Through <see cref="JsonDocument"/> rather than <c>JsonSerializer.Deserialize</c>, which is
    /// neither trim- nor AOT-safe. <c>Clone</c> because the element would otherwise point into the
    /// document's pooled buffer, which this method disposes.
    /// </remarks>
    private static JsonElement ToElement(JsonNode? node)
    {
        using var parsed = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return parsed.RootElement.Clone();
    }

    /// <summary>A canonical payload as JSON, with references flattened to the value the runtime resolves.</summary>
    private static JsonNode? ToNode(IEnumerable<KeyValuePair<string, object>> table, string documentExtension)
    {
        var result = new JsonObject();
        foreach (var (key, value) in table) result[key] = ToValue(value, documentExtension);
        return result;
    }

    private static JsonNode? ToValue(object? value, string documentExtension) => value switch
    {
        null => null,

        // An AssetReference becomes its PATH: the guid is how authoring survives a move, and the
        // runtime has a loader keyed on paths. An empty one is a deliberate null slot -- dropping
        // it would shift every material override after it onto the wrong primitive.
        CanonicalInlineTable reference => reference.Count == 0
            ? null
            : BuiltPath(reference.Value("path") as string, documentExtension),

        CanonicalTomlTable nested => ToNode(nested, documentExtension),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        long integer => JsonValue.Create(integer),
        double number => JsonValue.Create(number),
        IReadOnlyList<object> list => new JsonArray(list.Select(item => ToValue(item, documentExtension)).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>Where a referenced asset lands in the build.</summary>
    /// <remarks>
    /// Only authored documents move: <c>materials/x.toml</c> is compiled to whatever the profile's
    /// <c>document_format</c> produces, while a mesh or a bank is carried through under the name it
    /// already has.
    /// </remarks>
    private static JsonNode? BuiltPath(string? path, string documentExtension)
    {
        if (path is null) return null;
        return JsonValue.Create(path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(path.AsSpan(0, path.Length - ".toml".Length), documentExtension)
            : path);
    }
}
