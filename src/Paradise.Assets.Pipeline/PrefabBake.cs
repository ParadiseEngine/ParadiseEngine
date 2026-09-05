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
    public static PrefabData Bake(
        PrefabDocument document,
        Func<AssetReference, PrefabDocument?> prefabs,
        string documentExtension,
        List<string> errors,
        Func<AssetReference, string?>? builtPath = null)
        => Bake(document, prefabs, documentExtension, documentExtension, errors, builtPath);

    /// <param name="builtPath">
    /// Where the BUILD writes a reference's asset, by guid — <c>ImportContext.BuiltPath</c>, which asks
    /// the referenced asset's importer, so a texture becomes its KTX2. Authoritative when given: a
    /// null return means nothing is built for the reference and FAILS the bake at that site (throw
    /// a <see cref="FormatException"/> to say why), so no caller can hand the authored path on as
    /// if it were built — the authored path is a hint a rename leaves stale. Omitted, the bake
    /// falls back to the authored path with only the document extensions swapped, which is enough
    /// for a bake outside a build (tests, tools) and never for one the runtime reads.
    /// </param>
    public static PrefabData Bake(
        PrefabDocument document,
        Func<AssetReference, PrefabDocument?> prefabs,
        string prefabExtension,
        string configExtension,
        List<string> errors,
        Func<AssetReference, string?>? builtPath = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(errors);

        var extensions = new DocumentExtensions(prefabExtension, configExtension);
        Func<AssetReference, string> resolvePath = builtPath is null
            ? reference => Rebase(reference.Path, extensions)
            : reference => builtPath(reference)
                ?? throw new FormatException($"nothing is built for the reference '{reference.Path}' (guid {DocumentGuid.Format(reference.Guid)})");
        var resolved = PrefabResolver.Resolve(document, prefabs);
        foreach (var error in resolved.Errors) errors.Add(error.Message);

        var level = new PrefabData();
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
    // shift every material after it onto the wrong primitive. A path-only table (no guid) is a
    // reference no identity was minted for; its path is rebased by extension, the best that can be
    // said of it.
    private static JsonNode? ToNode(
        IEnumerable<KeyValuePair<string, object>> table, DocumentExtensions extensions, Func<AssetReference, string> builtPath)
        => CanonicalJson.ToNode(table, inline =>
        {
            if (AssetReferenceCodec.TryRead(inline, out var reference)) return JsonValue.Create(builtPath(reference));
            return inline.Value(AssetReferenceCodec.PathKey) is string path ? JsonValue.Create(Rebase(path, extensions)) : null;
        });

    /// <summary>The authored path with only the document extensions swapped — what can be known without a build.</summary>
    private static string Rebase(string path, DocumentExtensions extensions)
    {
        if (path.EndsWith(AssetClassifier.PrefabSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(path.AsSpan(0, path.Length - AssetClassifier.PrefabSuffix.Length), extensions.Prefab);
        }

        if (path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(path.AsSpan(0, path.Length - ".toml".Length), extensions.Config);
        }

        return path;
    }

    private readonly record struct DocumentExtensions(string Prefab, string Config);
}
