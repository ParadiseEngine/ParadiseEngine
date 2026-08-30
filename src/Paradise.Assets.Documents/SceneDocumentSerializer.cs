using Paradise.Authoring;

using Tomlyn.Model;

using Zio;

namespace Paradise.Assets.Documents;

/// <summary>
/// Reads and writes <c>*.scene</c> and <c>*.prefab</c> documents — the same shape, so the same
/// code.
/// </summary>
/// <remarks>
/// <para>
/// Reading is <b>strict</b>: unknown structural keys, malformed GUIDs, duplicate identities,
/// reserved payload names, dangling or cyclic parents are all errors naming the object. The
/// document is committed source of truth, and a reader that guessed would turn an authoring typo
/// into a build that succeeds and renders the wrong scene.
/// </para>
/// <para>
/// Writing is canonical (<see cref="CanonicalTomlWriter"/>), so read → write is byte-identical
/// for a canonical input and the Python mirror produces the same bytes. <c>scene-check</c>
/// polices exactly that.
/// </para>
/// <para>
/// The payload is <b>flat</b> — a component's fields sit beside <c>id</c>, <c>type</c> and
/// <c>removed</c> rather than under a nested table — so those three names are reserved and a
/// payload using one is refused here rather than silently swallowed as structure.
/// </para>
/// </remarks>
public static class SceneDocumentSerializer
{
    private static readonly string[] s_documentKeys = ["schema_version", "objects"];
    private static readonly string[] s_objectKeys = ["prefab", "components"];

    /// <summary>Reads and validates the document at <paramref name="path"/>.</summary>
    /// <exception cref="SceneDocumentException">The file is unreadable, not TOML, or not valid.</exception>
    public static SceneDocument Load(IFileSystem fileSystem, UPath path)
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
            throw new SceneDocumentException(path.FullName, $"could not be read ({error.Message})", error);
        }

        return Parse(text, path.FullName);
    }

    /// <summary>Validates already-read text. The filesystem-free half of <see cref="Load"/>.</summary>
    /// <exception cref="SceneDocumentException">The text is not TOML, or not a valid document.</exception>
    public static SceneDocument Parse(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);
        ArgumentNullException.ThrowIfNull(sourceName);

        Exception Fail(string problem) => new SceneDocumentException(sourceName, problem);

        var root = TomlDocumentReader.Parse(toml, Fail);
        TomlDocumentReader.RejectUnknownKeys(root, "at the document root", s_documentKeys, Fail);

        var schemaVersion = TomlDocumentReader.RequireInteger(root, "schema_version", "at the document root", Fail);
        if (schemaVersion != SceneDocument.SupportedSchemaVersion)
        {
            throw Fail(
                $"declares schema_version = {schemaVersion}, which this build cannot read " +
                $"(supported: {SceneDocument.SupportedSchemaVersion})");
        }

        var document = new SceneDocument();
        var parents = new Dictionary<Guid, Guid?>();
        if (TomlDocumentReader.OptionalTableArray(root, "objects", "at the document root", Fail) is { } objects)
        {
            foreach (var (table, index) in objects.Select(static (table, index) => (table, index)))
            {
                var sceneObject = ReadObject(table, index, Fail);

                // A Target carrier addresses a prefab-local object and has no identity of its
                // own -- the resolved child's guid is always minted. So it is exempt from the
                // uniqueness map, which is about identities the document actually declares.
                if (sceneObject.Target is not null)
                {
                    document.Objects.Add(sceneObject);
                    continue;
                }

                if (sceneObject.Guid is not { } guid)
                {
                    throw Fail($"has an object at index {index} with no '{WellKnownComponents.MetaType}' component carrying a '{WellKnownComponents.Guid}'");
                }

                if (!parents.TryAdd(guid, sceneObject.Parent))
                {
                    throw Fail($"declares guid '{DocumentGuid.Format(guid)}' twice — identities must be unique per document");
                }

                document.Objects.Add(sceneObject);
            }
        }

        ValidateParents(parents, Fail);
        return document;
    }

    /// <summary>Renders <paramref name="document"/> as canonical TOML text.</summary>
    public static string Write(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return CanonicalTomlWriter.WriteString(ToCanonical(document));
    }

    /// <summary>Writes <paramref name="document"/> to <paramref name="path"/> as UTF-8, no BOM.</summary>
    public static void Save(IFileSystem fileSystem, UPath path, SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));
        ArgumentNullException.ThrowIfNull(document);

        fileSystem.WriteAllBytes(path, CanonicalTomlWriter.WriteBytes(ToCanonical(document)));
    }

    private static SceneObject ReadObject(TomlTable table, int index, Func<string, Exception> fail)
    {
        var context = $"on objects[{index}]";
        TomlDocumentReader.RejectUnknownKeys(table, context, s_objectKeys, fail);

        var sceneObject = new SceneObject();

        if (table.TryGetValue("prefab", out var prefab))
        {
            var canonical = TomlDocumentReader.ToCanonicalValue(prefab, $"'prefab' {context}", fail);
            sceneObject.Prefab = AssetReferenceCodec.Read(canonical, context, fail)
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

            sceneObject.Components.Add(component);
        }

        return sceneObject;
    }

    private static SceneComponent ReadComponent(TomlTable table, string objectContext, Func<string, Exception> fail)
    {
        var context = $"on a component {objectContext}";

        if (!table.TryGetValue(SceneComponent.IdKey, out var idValue) || idValue is not string idText)
        {
            throw fail($"needs a string '{SceneComponent.IdKey}' {context}");
        }

        if (!DocumentGuid.TryParse(idText, out var id) || id == Guid.Empty)
        {
            throw fail($"holds '{idText}' where '{SceneComponent.IdKey}' {context} must be a non-empty UUID");
        }

        string? type = null;
        if (table.TryGetValue(SceneComponent.TypeKey, out var typeValue))
        {
            type = typeValue as string ?? throw fail($"holds a non-string '{SceneComponent.TypeKey}' {context}");
        }

        var removed = false;
        if (table.TryGetValue(SceneComponent.RemovedKey, out var removedValue))
        {
            removed = removedValue as bool? ?? throw fail($"holds a non-boolean '{SceneComponent.RemovedKey}' {context}");
        }

        // Everything else is payload. The reserved names are consumed above, so anything left
        // over that matches one would be a duplicate key, which TOML itself rejects.
        var data = new CanonicalTomlTable();
        foreach (var (key, value) in table)
        {
            if (key is SceneComponent.IdKey or SceneComponent.TypeKey or SceneComponent.RemovedKey) continue;
            data.Add(key, TomlDocumentReader.ToCanonicalValue(value, $"'{key}' {context}", fail));
        }

        if (removed && data.Count > 0)
        {
            // A dropped component carries no payload: the two together say "remove this, and also
            // here is what it should contain", which has no meaning and is almost certainly an
            // edit that forgot to delete one half.
            throw fail($"marks a component '{SceneComponent.RemovedKey}' but also gives it fields {context}");
        }

        return new SceneComponent(id, type, data, removed);
    }

    private static CanonicalTomlTable ToCanonical(SceneDocument document)
    {
        var root = new CanonicalTomlTable { { "schema_version", (long)SceneDocument.SupportedSchemaVersion } };
        if (document.Objects.Count > 0)
        {
            root.Add("objects", document.Objects.Select(ToCanonical).ToArray());
        }

        return root;
    }

    private static CanonicalTomlTable ToCanonical(SceneObject sceneObject)
    {
        var table = new CanonicalTomlTable();
        if (sceneObject.Prefab is { } prefab) table.Add("prefab", AssetReferenceCodec.Write(prefab));
        if (sceneObject.Components.Count > 0)
        {
            table.Add("components", sceneObject.Components.Select(ToCanonical).ToArray());
        }

        return table;
    }

    private static CanonicalTomlTable ToCanonical(SceneComponent component)
    {
        var table = new CanonicalTomlTable { { SceneComponent.IdKey, DocumentGuid.Format(component.Id) } };
        if (component.Type is { } type) table.Add(SceneComponent.TypeKey, type);
        if (component.Removed) table.Add(SceneComponent.RemovedKey, true);
        foreach (var (key, value) in component.Data) table.Add(key, value);
        return table;
    }

    /// <summary>
    /// Rejects dangling and cyclic parents. A dangling parent is an edit that deleted an object
    /// without reparenting its children; a cycle has no world transform at all — both must fail
    /// at read time, not as a stack overflow in the bake.
    /// </summary>
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
