using Paradise.Authoring;

using Tomlyn.Model;

using Zio;

namespace Paradise.Assets.Documents;

/// <summary>Reads (strictly, because a guessing reader turns a typo into a build that renders the wrong thing) and writes (canonically, so read → write is byte-identical) <c>*.prefab</c> documents.</summary>
public static class PrefabDocumentSerializer
{
    private static readonly string[] s_documentKeys = ["schema_version", "objects"];
    private static readonly string[] s_objectKeys = ["prefab", "components"];

    /// <exception cref="PrefabDocumentException">The file is unreadable, not TOML, or not valid.</exception>
    public static PrefabDocument Load(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));

        string text;
        try
        {
            text = fileSystem.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PrefabDocumentException(path.FullName, $"could not be read ({error.Message})", error);
        }

        return Parse(text, path.FullName);
    }

    /// <summary>The filesystem-free half of <see cref="Load"/>.</summary>
    public static PrefabDocument Parse(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);
        ArgumentNullException.ThrowIfNull(sourceName);

        Exception Fail(string problem) => new PrefabDocumentException(sourceName, problem);

        var root = TomlDocumentReader.Parse(toml, Fail);
        TomlDocumentReader.RejectUnknownKeys(root, "at the document root", s_documentKeys, Fail);

        var schemaVersion = TomlDocumentReader.RequireInteger(root, "schema_version", "at the document root", Fail);
        if (schemaVersion != PrefabDocument.SupportedSchemaVersion)
        {
            throw Fail(
                $"declares schema_version = {schemaVersion}, which this build cannot read " +
                $"(supported: {PrefabDocument.SupportedSchemaVersion})");
        }

        var document = new PrefabDocument();
        var parents = new Dictionary<Guid, Guid?>();
        var carrierParents = new List<(Guid Target, Guid Parent)>();
        if (TomlDocumentReader.OptionalTableArray(root, "objects", "at the document root", Fail) is { } objects)
        {
            foreach (var (table, index) in objects.Select(static (table, index) => (table, index)))
            {
                var entry = ReadObject(table, index, Fail);

                // A carrier addresses a prefab child; the resolved child's guid is minted, so a
                // carrier declaring one would be an identity nothing else can ever refer to.
                if (entry.Target is { } target)
                {
                    if (entry.Guid is not null)
                    {
                        throw Fail($"has an override carrier targeting '{DocumentGuid.Format(target)}' at index {index} that also declares a '{WellKnownComponents.Guid}'; a carrier has no identity of its own");
                    }

                    if (entry.Parent is not { } carrierParent)
                    {
                        throw Fail($"has an override carrier targeting '{DocumentGuid.Format(target)}' at index {index} with no '{WellKnownComponents.Parent}' naming the instance it sits under");
                    }

                    carrierParents.Add((target, carrierParent));
                    document.Objects.Add(entry);
                    continue;
                }

                if (entry.Guid is not { } guid)
                {
                    throw Fail($"has an object at index {index} with no '{WellKnownComponents.MetaType}' component carrying a '{WellKnownComponents.Guid}'");
                }

                if (!parents.TryAdd(guid, entry.Parent))
                {
                    throw Fail($"declares guid '{DocumentGuid.Format(guid)}' twice — identities must be unique per document");
                }

                document.Objects.Add(entry);
            }
        }

        ValidateParents(parents, Fail);
        foreach (var (target, parent) in carrierParents)
        {
            if (!parents.ContainsKey(parent))
            {
                throw Fail($"parents an override carrier targeting '{DocumentGuid.Format(target)}' to '{DocumentGuid.Format(parent)}', which does not exist");
            }
        }

        // Every read validates, so the resolver and the bake can take a single root for granted.
        document.Validate(sourceName);
        return document;
    }

    public static string Write(PrefabDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return CanonicalTomlWriter.WriteString(ToCanonical(document));
    }

    public static void Save(IFileSystem fileSystem, UPath path, PrefabDocument document)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));
        ArgumentNullException.ThrowIfNull(document);

        fileSystem.WriteAllBytes(path, CanonicalTomlWriter.WriteBytes(ToCanonical(document)));
    }

    private static PrefabObject ReadObject(TomlTable table, int index, Func<string, Exception> fail)
    {
        var context = $"on objects[{index}]";
        TomlDocumentReader.RejectUnknownKeys(table, context, s_objectKeys, fail);

        var result = new PrefabObject();

        if (table.TryGetValue("prefab", out var prefab))
        {
            var canonical = TomlDocumentReader.ToCanonicalValue(prefab, $"'prefab' {context}", fail);
            result.Prefab = AssetReferenceCodec.Read(canonical, context, fail)
                ?? throw fail($"has an empty 'prefab' reference {context}");
        }

        var seen = new HashSet<Guid>();
        foreach (var entry in TomlDocumentReader.OptionalTableArray(table, "components", context, fail) ?? [])
        {
            var component = ReadComponent(entry, context, fail);
            if (!seen.Add(component.Id))
            {
                throw fail($"declares component '{DocumentGuid.Format(component.Id)}' twice {context}");
            }

            result.Components.Add(component);
        }

        return result;
    }

    private static PrefabComponent ReadComponent(TomlTable table, string objectContext, Func<string, Exception> fail)
    {
        var context = $"on a component {objectContext}";

        if (!table.TryGetValue(PrefabComponent.IdKey, out var idValue) || idValue is not string idText)
        {
            throw fail($"needs a string '{PrefabComponent.IdKey}' {context}");
        }

        if (!DocumentGuid.TryParse(idText, out var id) || id == Guid.Empty)
        {
            throw fail($"holds '{idText}' where '{PrefabComponent.IdKey}' {context} must be a non-empty UUID");
        }

        string? type = null;
        if (table.TryGetValue(PrefabComponent.TypeKey, out var typeValue))
        {
            type = typeValue as string ?? throw fail($"holds a non-string '{PrefabComponent.TypeKey}' {context}");
        }

        var removed = false;
        if (table.TryGetValue(PrefabComponent.RemovedKey, out var removedValue))
        {
            removed = removedValue as bool? ?? throw fail($"holds a non-boolean '{PrefabComponent.RemovedKey}' {context}");
        }

        // The skip is what keeps reserved names out of the payload; Tomlyn's default does not
        // refuse duplicate keys (issue #198).
        var data = new CanonicalTomlTable();
        foreach (var (key, value) in table)
        {
            if (PrefabComponent.IsReserved(key)) continue;
            data.Add(key, TomlDocumentReader.ToCanonicalValue(value, $"'{key}' {context}", fail));
        }

        if (removed && data.Count > 0)
        {
            // "Remove this, and here is what it should contain" is an edit that forgot one half.
            throw fail($"marks a component '{PrefabComponent.RemovedKey}' but also gives it fields {context}");
        }

        var component = new PrefabComponent(id, type, data, removed);
        if (WellKnownComponents.PayloadProblem(component) is { } problem)
        {
            throw fail($"{problem} {context}");
        }

        return component;
    }

    private static CanonicalTomlTable ToCanonical(PrefabDocument document)
    {
        var root = new CanonicalTomlTable { { "schema_version", (long)PrefabDocument.SupportedSchemaVersion } };
        if (document.Objects.Count > 0)
        {
            root.Add("objects", document.Objects.Select(ToCanonical).ToArray());
        }

        return root;
    }

    private static CanonicalTomlTable ToCanonical(PrefabObject entry)
    {
        var table = new CanonicalTomlTable();
        if (entry.Prefab is { } prefab) table.Add("prefab", AssetReferenceCodec.Write(prefab));
        if (entry.Components.Count > 0)
        {
            table.Add("components", entry.Components.Select(ToCanonical).ToArray());
        }

        return table;
    }

    private static CanonicalTomlTable ToCanonical(PrefabComponent component)
    {
        // A tool building a malformed well-known payload fails here, not at the next read.
        if (WellKnownComponents.PayloadProblem(component) is { } problem)
        {
            throw new InvalidOperationException($"This document {problem}, so it cannot be written.");
        }

        var table = new CanonicalTomlTable { { PrefabComponent.IdKey, DocumentGuid.Format(component.Id) } };
        if (component.Type is { } type) table.Add(PrefabComponent.TypeKey, type);
        if (component.Removed) table.Add(PrefabComponent.RemovedKey, true);
        foreach (var (key, value) in component.Data) table.Add(key, value);
        return table;
    }

    private static void ValidateParents(Dictionary<Guid, Guid?> parents, Func<string, Exception> fail)
    {
        foreach (var (guid, parent) in parents)
        {
            if (parent is { } target && !parents.ContainsKey(target))
            {
                throw fail($"parents object '{DocumentGuid.Format(guid)}' to '{DocumentGuid.Format(target)}', which does not exist");
            }
        }

        foreach (var start in parents.Keys)
        {
            var slow = start;
            var current = start;
            var steps = 0;
            while (parents[current] is { } next)
            {
                current = next;
                if (++steps % 2 == 0) slow = parents[slow]!.Value;
                if (current == slow)
                {
                    throw fail($"has a parent cycle through object '{DocumentGuid.Format(current)}'");
                }
            }
        }
    }
}
