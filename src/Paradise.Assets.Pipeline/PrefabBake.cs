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
/// <b>Since contract v6, a passthrough — not a flatten.</b> Every component crosses over
/// untouched, carrying the id and type it was authored with, INCLUDING the well-known
/// <c>meta</c> and <c>transform</c> payloads: identity, the parent link and the local TRS
/// survive into the contract, and the loader composes world matrices itself. The bake that used
/// to privilege two engine components (a baked name, a flattened world matrix) is gone with
/// the engine's authored components.
/// </para>
/// <para>
/// What the bake still destroys:
/// </para>
/// <list type="bullet">
///   <item>instances become plain objects — resolved, recursively, so nothing downstream knows
///     prefabs exist (override carriers are consumed with them: resolved output never carries
///     <c>meta.Target</c> or <c>meta.Dropped</c>);</item>
///   <item>references become values — an <see cref="AssetReference"/>'s guid is an authoring
///     concern, and the runtime resolves a path.</item>
/// </list>
/// <para>
/// One rule the bake enforces on the way through: hierarchy ships now, so <b>a parented object
/// must carry a transform component</b>. Its absence is an error naming the object here,
/// rather than a silent identity placement decided by whichever loader meets it.
/// </para>
/// </remarks>
public static class PrefabBake
{
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

        var level = new LevelData();
        foreach (var entry in resolved.Document.Objects)
        {
            if (entry.Parent is not null && entry.Component(WellKnownComponents.TransformId) is null)
            {
                errors.Add(
                    $"object '{entry.Name ?? DocumentGuid.Format(entry.Guid ?? Guid.Empty)}' has a " +
                    "parent but no transform component; a parented object must carry one");
            }

            var components = new List<AuthoredComponentData>();
            foreach (var component in entry.Components)
            {
                components.Add(new AuthoredComponentData
                {
                    Id = component.Id,
                    Type = component.Type,
                    Data = ToElement(ToNode(component.Data, documentExtension)),
                });
            }

            level.Entities.Add(components);
        }

        return level;
    }

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
