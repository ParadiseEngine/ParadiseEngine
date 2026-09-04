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
        List<string> errors,
        Func<AssetReference, string>? currentPath = null)
        => Bake(document, prefabs, documentExtension, documentExtension, errors, currentPath);

    /// <param name="currentPath">
    /// Where a reference's asset lives NOW, by guid. Null keeps the authored path half, which is
    /// only a hint: a build that skipped this would flatten a stale path into the contract and
    /// ship a reference the runtime cannot open.
    /// </param>
    public static LevelData Bake(
        PrefabDocument document,
        Func<AssetReference, PrefabDocument?> prefabs,
        string prefabExtension,
        string configExtension,
        List<string> errors,
        Func<AssetReference, string>? currentPath = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(errors);

        var extensions = new DocumentExtensions(prefabExtension, configExtension);
        var resolvePath = currentPath ?? (reference => reference.Path);
        var resolved = PrefabResolver.Resolve(document, prefabs);
        foreach (var error in resolved.Errors) errors.Add(error.Message);

        var level = new LevelData();
        foreach (var (entry, index) in resolved.Document.Objects.Select(static (entry, index) => (entry, index)))
        {
            var components = new List<AuthoredComponentData>();
            foreach (var component in entry.Components)
            {
                JsonElement data;
                try
                {
                    data = ToElement(ToNode(component.Data, extensions, resolvePath));
                }
                catch (FormatException failure)
                {
                    // A value TOML can spell and JSON cannot (inf, nan). The importer contract is
                    // an error on the list, never an exception out of Import.
                    var name = entry.Name is { Length: > 0 } named ? named : DocumentGuid.Format(entry.Guid ?? Guid.Empty);
                    errors.Add($"object {index} ({name}), component {component.Type ?? DocumentGuid.Format(component.Id)}: {failure.Message}");
                    continue;
                }

                components.Add(new AuthoredComponentData
                {
                    Id = component.Id,
                    Type = component.Type,
                    Data = data,
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
    private static JsonNode? ToNode(
        IEnumerable<KeyValuePair<string, object>> table, DocumentExtensions extensions, Func<AssetReference, string> currentPath)
        => CanonicalJson.ToNode(table, inline => BuiltPath(AuthoringPath(inline, currentPath), extensions));

    private static string? AuthoringPath(CanonicalInlineTable table, Func<AssetReference, string> currentPath)
        => AssetReferenceCodec.TryRead(table, out var reference)
            ? currentPath(reference)
            : table.Value(AssetReferenceCodec.PathKey) as string;

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
