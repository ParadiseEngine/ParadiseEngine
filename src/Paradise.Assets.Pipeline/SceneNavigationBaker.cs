using System.Security.Cryptography;
using System.Text;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf;
using Paradise.Assets.Project;
using Paradise.Authoring;
using Paradise.Export.Data;
using Paradise.Export.NavMesh;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The generated navigation path and whether its canonical component changed.</summary>
public sealed record SceneNavigationPathResult(UPath Output, string RelativePath, string DocumentHash, bool DocumentChanged);

/// <summary>A published scene navigation asset and its display geometry.</summary>
public sealed record SceneNavigationBakeResult(
    UPath Output, string RelativePath, NavMeshPreview Preview, string DocumentHash, bool DocumentChanged);

/// <summary>Bakes a canonical level through schema-defined static geometry without depending on game types.</summary>
public static class SceneNavigationBaker
{
    public const string NavigationHostKind = "navmesh";
    public const string GeometryHostKind = "navmesh-geometry";
    private static readonly Guid s_rigidbodyId = Guid.Parse("b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11");

    public static SceneNavigationBakeResult Bake(IFileSystem fileSystem, AssetProjectLayout layout,
        UPath document, AuthoringSchemaDocument schema, Guid componentId, Guid? entityId = null,
        NavMeshBakeSettings? settings = null, string? expectedDocumentHash = null)
    {
        var target = ReadTarget(fileSystem, layout, document, schema, componentId, entityId, expectedDocumentHash);
        var index = AssetIndex.Scan(fileSystem, layout.Assets);
        var errors = new List<string>();
        var scene = SceneGeometry.Load(fileSystem, index, document, errors);
        var geometry = Geometry(fileSystem, index, scene, schema, errors);
        if (errors.Count != 0)
            throw new InvalidDataException($"Cannot bake navigation for '{document}':\n{string.Join("\n", errors)}");
        geometry.Settings = settings ?? new NavMeshBakeSettings();
        var baked = NavMeshBakeService.Bake(geometry);
        var normalized = NormalizePayload(target);
        using var binary = new StagedOutput(fileSystem, target.Output);
        using var authored = normalized.Changed ? new StagedOutput(fileSystem, document) : null;
        binary.Write(baked.Bytes);
        authored?.Write(normalized.Bytes);
        EnsureCurrent(fileSystem, document, target.Bytes);
        binary.Commit();
        try
        {
            authored?.Commit();
        }
        catch
        {
            binary.Rollback();
            throw;
        }
        return new SceneNavigationBakeResult(target.Output, target.RelativePath, baked.Preview,
            Hash(normalized.Bytes), normalized.Changed);
    }

    /// <summary>Derives and persists the generated path without baking or touching the existing binary.</summary>
    public static SceneNavigationPathResult Normalize(IFileSystem fileSystem, AssetProjectLayout layout,
        UPath document, AuthoringSchemaDocument schema, Guid componentId, Guid? entityId = null,
        string? expectedDocumentHash = null)
    {
        var target = ReadTarget(fileSystem, layout, document, schema, componentId, entityId, expectedDocumentHash);
        var normalized = NormalizePayload(target);
        if (normalized.Changed)
        {
            using var authored = new StagedOutput(fileSystem, document);
            authored.Write(normalized.Bytes);
            EnsureCurrent(fileSystem, document, target.Bytes);
            authored.Commit();
        }
        return new SceneNavigationPathResult(target.Output, target.RelativePath, Hash(normalized.Bytes), normalized.Changed);
    }

    /// <summary>Reads the scene's derived navigation asset without rebaking or updating authored data.</summary>
    public static NavMeshPreview Preview(IFileSystem fileSystem, AssetProjectLayout layout,
        UPath document, AuthoringSchemaDocument schema, Guid componentId, Guid? entityId = null)
    {
        var target = ReadTarget(fileSystem, layout, document, schema, componentId, entityId, null);
        return NavMeshBakeService.Preview(fileSystem.ReadAllBytes(target.Output));
    }

    private static NavMeshBakeInput Geometry(IFileSystem fileSystem, AssetIndex index,
        SceneGeometry.Scene scene, AuthoringSchemaDocument schema, List<string> errors)
    {
        var schemas = schema.Components.ToDictionary(component => component.Id);
        var geometryCache = new Dictionary<Guid, GltfAsset>();
        var meshes = new Dictionary<Guid, MeshReferenceDocument>();
        var excluded = new Dictionary<SceneGeometry.Entry, bool>();
        var vertices = new List<float>();
        var indices = new List<int>();
        foreach (var entry in scene.Objects)
        {
            if (IsExcluded(entry) || entry.World is not { } world) continue;
            var seen = new HashSet<Guid>();
            foreach (var component in entry.Object.Components)
            {
                schemas.TryGetValue(component.Id, out var componentSchema);
                foreach (var (_, value) in SceneGeometry.HostValues(component.Data, componentSchema, AuthoredBySources.Mesh))
                {
                    var reference = ReadReference(value, entry.Object.Name);
                    if (reference is null || !seen.Add(reference.Guid)) continue;
                    SceneGeometry.AppendMeshTriangles(fileSystem, index, reference, world, vertices, indices, errors, geometryCache);
                }
            }
        }
        var points = new float[vertices.Count / 3][];
        for (var point = 0; point < points.Length; point++)
            points[point] = [vertices[point * 3], vertices[point * 3 + 1], vertices[point * 3 + 2]];
        return new NavMeshBakeInput { Vertices = points, Indices = indices.ToArray() };

        bool IsExcluded(SceneGeometry.Entry entry)
        {
            if (excluded.TryGetValue(entry, out var result)) return result;
            // Resolve reports hierarchy cycles; mark before walking so a failed resolution cannot recurse forever.
            excluded.Add(entry, true);
            if (scene.ParentOf(entry) is { } parent && IsExcluded(parent)) return true;
            foreach (var component in entry.Object.Components)
            {
                schemas.TryGetValue(component.Id, out var componentSchema);
                if (component.Id == s_rigidbodyId)
                {
                    var field = componentSchema?.Fields.Find(candidate => candidate.Name == "BodyType");
                    var fallback = field?.Default is { ValueKind: System.Text.Json.JsonValueKind.String } defaultValue
                        ? defaultValue.GetString() : nameof(PhysicsBodyType.Dynamic);
                    var bodyType = component.Data.Value("BodyType") ?? fallback;
                    if (bodyType is nameof(PhysicsBodyType.Dynamic) or nameof(PhysicsBodyType.Kinematic)) return true;
                    if (bodyType is not (nameof(PhysicsBodyType.Static) or nameof(PhysicsBodyType.None)))
                        errors.Add($"object '{entry.Object.Name}': rigidbody BodyType '{bodyType}' is invalid");
                }
                foreach (var (field, value) in SceneGeometry.HostValues(component.Data, componentSchema, GeometryHostKind))
                {
                    var participates = value ?? (field.Default is { } fallback && fallback.ValueKind == System.Text.Json.JsonValueKind.False ? false : true);
                    if (participates is false) return true;
                    if (participates is not true)
                        errors.Add($"object '{entry.Object.Name}': navigation geometry field '{field.Name}' must be a boolean");
                }
                foreach (var (field, value) in SceneGeometry.HostValues(component.Data, componentSchema, AuthoredBySources.Mesh))
                {
                    if (field.AssetKinds is { Count: > 0 } kinds &&
                        kinds.All(kind => string.Equals(kind, MeshReferenceDocument.SkinnedMeshSuffix, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            // Participation is independent of component order, so inspect references only after
            // all host-owned exclusion declarations on this object have been considered.
            foreach (var component in entry.Object.Components)
            {
                schemas.TryGetValue(component.Id, out var componentSchema);
                foreach (var (_, value) in SceneGeometry.HostValues(component.Data, componentSchema, AuthoredBySources.Mesh))
                {
                    var reference = ReadReference(value, entry.Object.Name);
                    if (reference is null) continue;
                    if (!meshes.TryGetValue(reference.Guid, out var mesh))
                    {
                        var resolved = index.Resolve(reference);
                        if (!resolved.Found)
                        {
                            errors.Add($"object '{entry.Object.Name}': mesh '{reference.Path}' ({reference.Guid}) does not resolve under assets/");
                            continue;
                        }
                        try
                        {
                            mesh = MeshReferenceDocument.Load(fileSystem, resolved.Asset);
                            meshes.Add(reference.Guid, mesh);
                        }
                        catch (Exception failure) when (failure is FormatException or IOException)
                        {
                            errors.Add(failure.Message);
                            continue;
                        }
                    }
                    if (mesh.Slot == MeshSlot.SkinnedMesh) return true;
                }
            }
            excluded[entry] = false;
            return false;
        }

        AssetReference? ReadReference(object? value, string? name)
        {
            if (value is null) return null;
            try
            {
                if (value is CanonicalTomlTable table)
                {
                    var inline = new CanonicalInlineTable();
                    foreach (var pair in table) inline.Add(pair.Key, pair.Value);
                    value = inline;
                }
                return AssetReferenceCodec.Read(value, $"on object '{name}'", message => new InvalidDataException(message));
            }
            catch (InvalidDataException failure)
            {
                errors.Add(failure.Message);
                return null;
            }
        }
    }

    private static Target ReadTarget(IFileSystem fileSystem, AssetProjectLayout layout, UPath document,
        AuthoringSchemaDocument schema, Guid componentId, Guid? entityId, string? expectedDocumentHash)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(schema);
        if (!document.IsAbsolute || !document.IsInDirectory(layout.Assets, recursive: true) ||
            !string.Equals(document.GetExtensionWithDot(), ".prefab", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Navigation actions require an absolute .prefab document under the project's assets/.", nameof(document));
        var componentSchema = schema.Components.SingleOrDefault(component => component.Id == componentId)
            ?? throw new InvalidDataException($"Navigation component '{componentId}' is absent from the authoring schema.");
        var fields = componentSchema.Fields.Where(field => field.AuthoredBy == NavigationHostKind).ToArray();
        if (fields.Length != 1 || fields[0].Type != AuthoredFieldTypes.String)
            throw new InvalidDataException("A navigation component must declare exactly one string field authored by 'navmesh'.");
        var bytes = fileSystem.ReadAllBytes(document);
        if (expectedDocumentHash is not null && !string.Equals(Hash(bytes), expectedDocumentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{document}' changed since the authoring action was requested; save and retry.");
        var parsed = PrefabDocumentSerializer.Parse(Encoding.UTF8.GetString(bytes), document.FullName);
        var objects = parsed.Objects.Where(obj => (entityId is null || obj.Guid == entityId) &&
            obj.Component(componentId) is { Removed: false }).ToArray();
        if (objects.Length != 1)
            throw new InvalidDataException($"Navigation action requires one canonical '{componentId}' component; found {objects.Length}.");
        var output = document.ChangeExtension(".navmesh");
        var relative = output.FullName[(layout.Assets.FullName.Length + 1)..];
        return new Target(bytes, parsed, objects[0], objects[0].Component(componentId)!, fields[0].Name, output, relative);
    }

    private static (byte[] Bytes, bool Changed) NormalizePayload(Target target)
    {
        if (target.Component.Data.Value(target.Field) is string current && current == target.RelativePath)
            return (target.Bytes, false);
        var payload = new CanonicalTomlTable();
        foreach (var (key, value) in target.Component.Data)
            payload.Add(key, key == target.Field ? target.RelativePath : value);
        if (!payload.ContainsKey(target.Field)) payload.Add(target.Field, target.RelativePath);
        var component = new PrefabComponent(target.Component.Id, target.Component.Type, payload);
        target.Object.Components[target.Object.Components.IndexOf(target.Component)] = component;
        return (Encoding.UTF8.GetBytes(PrefabDocumentSerializer.Write(target.Document)), true);
    }

    private static void EnsureCurrent(IFileSystem fileSystem, UPath document, byte[] original)
    {
        if (!fileSystem.ReadAllBytes(document).AsSpan().SequenceEqual(original))
            throw new InvalidDataException($"'{document}' changed during navigation baking; save and retry.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Target(byte[] Bytes, PrefabDocument Document, PrefabObject Object,
        PrefabComponent Component, string Field, UPath Output, string RelativePath);

    private sealed class StagedOutput(IFileSystem fileSystem, UPath destination) : IDisposable
    {
        private readonly UPath _temporary = destination.GetDirectory() / $".{destination.GetName()}.{Guid.NewGuid():N}.tmp";
        private readonly UPath _backup = destination.GetDirectory() / $".{destination.GetName()}.{Guid.NewGuid():N}.tmp";
        private bool _replaced;
        private bool _published;
        private bool _preserveBackup;

        public void Write(byte[] bytes)
        {
            fileSystem.CreateDirectory(destination.GetDirectory());
            using var stream = fileSystem.OpenFile(_temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
        }

        public void Commit()
        {
            _replaced = fileSystem.FileExists(destination);
            if (_replaced) fileSystem.ReplaceFile(_temporary, destination, _backup, false);
            else fileSystem.MoveFile(_temporary, destination);
            _published = true;
        }

        public void Rollback()
        {
            if (!_published) return;
            try
            {
                if (_replaced) fileSystem.ReplaceFile(_backup, destination, default, false);
                else fileSystem.DeleteFile(destination);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                _preserveBackup = _replaced;
                throw new IOException(_replaced
                    ? $"Could not restore '{destination}'; the previous file is preserved at '{_backup}'."
                    : $"Could not remove the newly published '{destination}' after the document update failed.", failure);
            }
            _published = false;
        }

        public void Dispose()
        {
            if (fileSystem.FileExists(_temporary)) fileSystem.DeleteFile(_temporary);
            if (!_preserveBackup && fileSystem.FileExists(_backup)) fileSystem.DeleteFile(_backup);
        }
    }
}
