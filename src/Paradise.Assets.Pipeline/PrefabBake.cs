using System.Text.Json;
using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Authoring;
using Paradise.Export.Data;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Turns an authoring document into the export contract. Since contract v6 a passthrough, not a
/// flatten: only instances (resolved away) and references (guid dropped, path kept) change, and
/// presence rules belong to whoever gives a payload meaning, never the engine.
/// </summary>
public static class PrefabBake
{
    public static LevelData Bake(
        PrefabDocument document,
        Func<AssetReference, PrefabDocument?> prefabs,
        string documentExtension,
        List<string> errors)
        => Bake(document, prefabs, documentExtension, documentExtension, errors);

    public static LevelData Bake(
        PrefabDocument document,
        Func<AssetReference, PrefabDocument?> prefabs,
        string prefabExtension,
        string configExtension,
        List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(errors);

        var extensions = new DocumentExtensions(prefabExtension, configExtension);
        var resolved = PrefabResolver.Resolve(document, prefabs);
        foreach (var error in resolved.Errors) errors.Add(error.Message);

        var level = new LevelData();
        foreach (var entry in resolved.Document.Objects)
        {
            var components = new List<AuthoredComponentData>();
            foreach (var component in entry.Components)
            {
                components.Add(new AuthoredComponentData
                {
                    Id = component.Id,
                    Type = component.Type,
                    Data = ToElement(ToNode(component.Data, extensions)),
                });
            }

            level.Entities.Add(components);
        }

        return level;
    }

    private static JsonElement ToElement(JsonNode? node)
    {
        using var parsed = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return parsed.RootElement.Clone();
    }

    // A reference bakes to its built path; a null slot ({}) stays null, because dropping it would
    // shift every material after it onto the wrong primitive.
    private static JsonNode? ToNode(IEnumerable<KeyValuePair<string, object>> table, DocumentExtensions extensions)
        => CanonicalJson.ToNode(table, reference => BuiltPath(reference.Value("path") as string, extensions));

    private static JsonNode? BuiltPath(string? path, DocumentExtensions extensions)
    {
        if (path is null) return null;

        if (path.EndsWith(AssetClassifier.PrefabSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(string.Concat(
                path.AsSpan(0, path.Length - AssetClassifier.PrefabSuffix.Length),
                extensions.Prefab));
        }

        if (path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(string.Concat(path.AsSpan(0, path.Length - ".toml".Length), extensions.Config));
        }

        return JsonValue.Create(path);
    }

    private readonly record struct DocumentExtensions(string Prefab, string Config);
}
