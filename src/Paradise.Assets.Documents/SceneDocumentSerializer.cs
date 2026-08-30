using System.Numerics;

using Tomlyn.Model;

using Zio;

namespace Paradise.Assets.Documents;

/// <summary>
/// Reads and writes <c>*.scene</c> documents.
/// </summary>
/// <remarks>
/// <para>
/// Reading is <b>strict</b>: unknown keys, malformed GUIDs, duplicate identities, dangling or
/// cyclic parents are all errors naming the object and the key — never a silent skip. The
/// document is committed source of truth; a reader that guessed would turn an authoring typo
/// into a build that succeeds and renders the wrong scene.
/// </para>
/// <para>
/// Writing is canonical (<see cref="CanonicalTomlWriter"/>), so read → write is byte-identical
/// for a canonical input, and the Python mirror produces the same bytes for the same scene.
/// An identity transform is omitted on write and defaulted on read — the common case (a plain
/// child of the world origin does not exist in practice, but freshly minted objects do) stays
/// one line in a diff.
/// </para>
/// </remarks>
public static class SceneDocumentSerializer
{
    private static readonly string[] s_documentKeys = ["schema_version", "objects"];
    private static readonly string[] s_objectKeys = ["guid", "name", "parent", "transform", "components"];
    private static readonly string[] s_transformKeys = ["position", "rotation", "scale"];
    private static readonly string[] s_componentKeys = ["id", "type", "data"];

    /// <summary>Reads and validates the document at <paramref name="path"/>.</summary>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="path">Absolute path of the document.</param>
    /// <exception cref="SceneDocumentException">The file is unreadable, not TOML, or not a valid scene.</exception>
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

    /// <summary>Validates an already-read document. The filesystem-free half of <see cref="Load"/>.</summary>
    /// <param name="toml">The document text.</param>
    /// <param name="sourceName">What to call the source in error messages.</param>
    /// <exception cref="SceneDocumentException">The text is not TOML, or not a valid scene.</exception>
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
                if (!parents.TryAdd(sceneObject.Guid, sceneObject.Parent))
                {
                    throw Fail($"declares guid '{DocumentGuid.Format(sceneObject.Guid)}' twice — identities must be unique per document");
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

    /// <summary>Writes <paramref name="document"/> to <paramref name="path"/> as UTF-8 without BOM.</summary>
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

        var guidText = TomlDocumentReader.RequireString(table, "guid", context, fail);
        if (!DocumentGuid.TryParse(guidText, out var guid) || guid == Guid.Empty)
        {
            throw fail($"holds '{guidText}' where 'guid' {context} must be a non-empty UUID");
        }

        var name = TomlDocumentReader.RequireString(table, "name", context, fail);
        if (name.Length == 0) throw fail($"needs a non-empty 'name' {context}");

        var sceneObject = new SceneObject(guid, name);
        context = $"on object '{DocumentGuid.Format(guid)}'";

        if (TomlDocumentReader.OptionalString(table, "parent", context, fail) is { } parentText)
        {
            if (!DocumentGuid.TryParse(parentText, out var parent) || parent == Guid.Empty)
            {
                throw fail($"holds '{parentText}' where 'parent' {context} must be a non-empty UUID");
            }

            sceneObject.Parent = parent;
        }

        if (TomlDocumentReader.OptionalTable(table, "transform", context, fail) is { } transform)
        {
            sceneObject.Transform = ReadTransform(transform, context, fail);
        }

        if (TomlDocumentReader.OptionalTableArray(table, "components", context, fail) is { } components)
        {
            var seen = new HashSet<Guid>();
            foreach (var component in components)
            {
                var entry = ReadComponent(component, context, fail);
                if (!seen.Add(entry.Id))
                {
                    throw fail($"declares component '{DocumentGuid.Format(entry.Id)}' twice {context}");
                }

                sceneObject.Components.Add(entry);
            }
        }

        return sceneObject;
    }

    private static SceneTransform ReadTransform(TomlTable table, string context, Func<string, Exception> fail)
    {
        context = $"in the transform {context}";
        TomlDocumentReader.RejectUnknownKeys(table, context, s_transformKeys, fail);

        var position = TomlDocumentReader.RequireFloatArray(table, "position", 3, context, fail);
        var rotation = TomlDocumentReader.RequireFloatArray(table, "rotation", 4, context, fail);
        var scale = TomlDocumentReader.RequireFloatArray(table, "scale", 3, context, fail);
        return new SceneTransform(
            new Vector3(position[0], position[1], position[2]),
            new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]),
            new Vector3(scale[0], scale[1], scale[2]));
    }

    private static SceneComponent ReadComponent(TomlTable table, string objectContext, Func<string, Exception> fail)
    {
        var context = $"on a component {objectContext}";
        TomlDocumentReader.RejectUnknownKeys(table, context, s_componentKeys, fail);

        var idText = TomlDocumentReader.RequireString(table, "id", context, fail);
        if (!DocumentGuid.TryParse(idText, out var id) || id == Guid.Empty)
        {
            throw fail($"holds '{idText}' where 'id' {context} must be a non-empty UUID");
        }

        var type = TomlDocumentReader.OptionalString(table, "type", context, fail);
        CanonicalTomlTable? data = null;
        if (TomlDocumentReader.OptionalTable(table, "data", context, fail) is { } payload)
        {
            data = TomlDocumentReader.ToCanonical(payload, $"in the '{DocumentGuid.Format(id)}' payload {objectContext}", fail);
        }

        return new SceneComponent(id, type, data);
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
        var table = new CanonicalTomlTable
        {
            { "guid", DocumentGuid.Format(sceneObject.Guid) },
            { "name", sceneObject.Name },
        };

        if (sceneObject.Parent is { } parent) table.Add("parent", DocumentGuid.Format(parent));

        if (sceneObject.Transform != SceneTransform.Identity)
        {
            var transform = sceneObject.Transform;
            table.Add("transform", new CanonicalTomlTable
            {
                { "position", new object[] { transform.Position.X, transform.Position.Y, transform.Position.Z } },
                { "rotation", new object[] { transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W } },
                { "scale", new object[] { transform.Scale.X, transform.Scale.Y, transform.Scale.Z } },
            });
        }

        if (sceneObject.Components.Count > 0)
        {
            table.Add("components", sceneObject.Components.Select(ToCanonical).ToArray());
        }

        return table;
    }

    private static CanonicalTomlTable ToCanonical(SceneComponent component)
    {
        var table = new CanonicalTomlTable { { "id", DocumentGuid.Format(component.Id) } };
        if (component.Type is { } type) table.Add("type", type);
        if (component.Data.Count > 0) table.Add("data", component.Data);
        return table;
    }
}
