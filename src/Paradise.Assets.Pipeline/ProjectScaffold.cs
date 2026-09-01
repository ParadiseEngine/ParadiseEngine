using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Writes a new asset project: the manifest, a sample level, and everything it references.
/// </summary>
/// <remarks>
/// <para>
/// <b>The output must pass <see cref="ProjectVerifier"/> and <see cref="BuildRunner"/> with zero
/// errors.</b> That is the whole specification, and it is what separates a scaffold from a
/// directory of plausible-looking files: every asset carries a sidecar with a GUID, every
/// reference resolves, every document is byte-canonical, and the level bakes.
/// </para>
/// <para>
/// It writes through <see cref="IFileSystem"/> rather than <c>System.IO</c> so the whole thing
/// runs against a memory filesystem in a test — which is where that specification is asserted.
/// </para>
/// <para>
/// The sample is deliberately small but not degenerate: a floor and three instances of one cube
/// prefab. Instances are the point — a sample without them would parse and build while
/// demonstrating none of what the format is for.
/// </para>
/// </remarks>
public static class ProjectScaffold
{
    /// <summary>A file the scaffold wrote, relative to the project root.</summary>
    /// <param name="Path">Where it landed.</param>
    /// <param name="Description">What it is, for the CLI to print.</param>
    public readonly record struct ScaffoldedFile(UPath Path, string Description);

    private const string MeshComponentType = "Paradise.Sample.Authoring.SampleMesh";
    private const string MaterialsComponentType = "Paradise.Export.Data.MaterialsComponentData";

    // Fixed component ids for the sample's two game-ish components. Arbitrary but STABLE: a
    // scaffold that minted fresh ids every run would produce a different document each time and
    // make two scaffolded projects incomparable.
    private static readonly Guid s_meshId = Guid.Parse("6b1f0c7e-2a44-4f18-9c3d-5e7a8b0d1f26");
    private static readonly Guid s_materialsId = Guid.Parse("bdc4fc87-d7b4-41f1-bc90-fc827005adfc");

    private const string LevelPath = "levels/main" + AssetClassifier.PrefabSuffix;
    private const string CubePrefabPath = "prefabs/cube" + AssetClassifier.PrefabSuffix;
    private const string MaterialPath = "materials/default.toml";
    private const string MeshPath = "Models/cube.glb";

    /// <summary>Creates the project at <paramref name="root"/>.</summary>
    /// <param name="fileSystem">Where to write.</param>
    /// <param name="root">The project directory. Created if absent; must be empty if present.</param>
    /// <param name="name">The project name, recorded in the manifest.</param>
    /// <exception cref="IOException">The directory exists and is not empty.</exception>
    public static IReadOnlyList<ScaffoldedFile> Create(IFileSystem fileSystem, UPath root, string name)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (fileSystem.DirectoryExists(root) && fileSystem.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException($"'{root}' already exists and is not empty");
        }

        var assets = root / AssetProjectLayout.AssetsDirectoryName;
        var written = new List<ScaffoldedFile>();

        // Nothing here is derived, so both trees are ignored wholesale -- the acceptance invariant
        // the asset plan states: deleting build/ and .editor/ loses nothing.
        Write(fileSystem, root / ".gitignore", "build/\n.editor/\n", written, "git ignores for derived output");

        WriteDocument(fileSystem, assets, AssetProjectLayout.ManifestFileName, Manifest(name), written, "project manifest");
        WriteDocument(fileSystem, assets, MaterialPath, Material(), written, "a flat-colour material");
        WriteBinary(fileSystem, assets, MeshPath, UnitCubeGlb(), written, "a unit cube");

        var mesh = Reference(fileSystem, assets, MeshPath);
        var material = Reference(fileSystem, assets, MaterialPath);

        WriteDocument(fileSystem, assets, CubePrefabPath,
            PrefabDocumentSerializer.Write(CubePrefab(mesh, material)), written, "the cube prefab");

        var cube = Reference(fileSystem, assets, CubePrefabPath);
        WriteDocument(fileSystem, assets, LevelPath,
            PrefabDocumentSerializer.Write(Level(name, cube, mesh, material)), written, "a level: a floor and three cubes");

        return written;
    }

    // ---- documents ------------------------------------------------------------------------

    private static string Manifest(string name)
    {
        var profiles = new CanonicalTomlTable
        {
            {
                "profiles", new CanonicalTomlTable
                {
                    // json, because that is the format the runtime's loader reads. The build can
                    // emit the contract as TOML too (BuildProfile.Default does), but a scaffolded
                    // profile exists to produce a playable tree, so it names what the engine
                    // consumes.
                    { "dev", new CanonicalTomlTable { { "document_format", "json" }, { "texture_quality", "fast" } } },
                    { "release", new CanonicalTomlTable { { "document_format", "json" }, { "texture_quality", "full" } } },
                }
            },
        };

        return CanonicalTomlWriter.WriteString(new CanonicalTomlTable
        {
            { "name", name },
            { "schema_version", (long)1 },
            { "build", profiles },
        });
    }

    private static string Material() => CanonicalTomlWriter.WriteString(new CanonicalTomlTable
    {
        { "Name", "default" },
        { "MetallicFactor", 0.0 },
        { "RoughnessFactor", 0.6 },
        {
            "BaseColorFactor", new CanonicalTomlTable
            {
                { "r", 0.62 }, { "g", 0.64 }, { "b", 0.68 }, { "a", 1.0 },
            }
        },
    });

    /// <summary>The cube prefab: one root carrying a mesh and a material.</summary>
    private static PrefabDocument CubePrefab(AssetReference mesh, AssetReference material)
    {
        var root = PrefabObject.WithMeta(Guid.Parse("c0bec0be-0000-4000-8000-000000000001"), "Cube");
        root.Components.Add(Transform(0, 0, 0, 1, 1, 1));
        root.Components.Add(new PrefabComponent(s_meshId, MeshComponentType,
            new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(mesh) } }));
        root.Components.Add(new PrefabComponent(s_materialsId, MaterialsComponentType,
            new CanonicalTomlTable { { "Slots", new object[] { AssetReferenceCodec.Write(material) } } }));

        var document = new PrefabDocument();
        document.Objects.Add(root);
        return document;
    }

    /// <summary>The level: a root, a flattened floor, and three instances of the cube prefab.</summary>
    private static PrefabDocument Level(string name, AssetReference cube, AssetReference mesh, AssetReference material)
    {
        var rootGuid = Guid.Parse("5cede000-0000-4000-8000-000000000001");
        var root = PrefabObject.WithMeta(rootGuid, name);
        root.Components.Add(Transform(0, 0, 0, 1, 1, 1));

        var document = new PrefabDocument();
        document.Objects.Add(root);

        // The floor is a plain object, so the sample shows both ways of placing something: an
        // object that owns its components, and an instance that inherits them.
        var floor = PrefabObject.WithMeta(Guid.Parse("5cede000-0000-4000-8000-000000000002"), "Floor", rootGuid);
        floor.Components.Add(Transform(0f, -0.5f, 0f, 20f, 1f, 20f));
        floor.Components.Add(new PrefabComponent(s_meshId, MeshComponentType,
            new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(mesh) } }));
        floor.Components.Add(new PrefabComponent(s_materialsId, MaterialsComponentType,
            new CanonicalTomlTable { { "Slots", new object[] { AssetReferenceCodec.Write(material) } } }));
        document.Objects.Add(floor);

        // Three instances of ONE prefab, each overriding only its transform -- which is the whole
        // argument for prefabs in three lines of document.
        for (var i = 0; i < 3; i++)
        {
            var instance = PrefabObject.WithMeta(
                Guid.Parse($"5cede000-0000-4000-8000-00000000001{i}"), $"Cube_{i}", rootGuid);
            instance.Prefab = cube;
            instance.Components.Add(Transform((i - 1) * 2f, 0.5f, 0f, 1f, 1f, 1f));
            document.Objects.Add(instance);
        }

        return document;
    }

    private static PrefabComponent Transform(float x, float y, float z, float sx, float sy, float sz)
        => LocalTransformCodec.Write(new LocalTransform(new Vector3(x, y, z), Quaternion.Identity, new Vector3(sx, sy, sz)));

    // ---- the mesh -------------------------------------------------------------------------

    /// <summary>
    /// A 1×1×1 cube centred on the origin, as a GLB.
    /// </summary>
    /// <remarks>
    /// Generated rather than shipped as an embedded blob: a base64 constant would be shorter and
    /// completely unreviewable, and this is the one asset a reader might actually want to check.
    /// 24 vertices rather than 8 because each face needs its own normal — a shared corner would
    /// average three normals and render the cube as a smooth blob.
    /// </remarks>
    private static byte[] UnitCubeGlb()
    {
        // +X, -X, +Y, -Y, +Z, -Z. Each face: four corners in counter-clockwise winding seen from
        // outside, so the default back-face culling keeps them.
        int[][] faces =
        [
            [1, 5, 7, 3], [4, 0, 2, 6], [2, 3, 7, 6], [4, 5, 1, 0], [5, 4, 6, 7], [0, 1, 3, 2],
        ];
        float[][] normals =
        [
            [1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1],
        ];

        var positions = new List<float>();
        var normalData = new List<float>();
        var indices = new List<ushort>();

        for (var face = 0; face < 6; face++)
        {
            var baseIndex = (ushort)(face * 4);
            foreach (var corner in faces[face])
            {
                // Corner bit 0 = X, bit 1 = Y, bit 2 = Z; 0 -> -0.5, 1 -> +0.5.
                positions.Add(((corner & 1) == 0 ? -0.5f : 0.5f));
                positions.Add(((corner & 2) == 0 ? -0.5f : 0.5f));
                positions.Add(((corner & 4) == 0 ? -0.5f : 0.5f));
                normalData.AddRange(normals[face]);
            }

            indices.AddRange([baseIndex, (ushort)(baseIndex + 1), (ushort)(baseIndex + 2)]);
            indices.AddRange([baseIndex, (ushort)(baseIndex + 2), (ushort)(baseIndex + 3)]);
        }

        var positionBytes = Floats(positions);
        var normalBytes = Floats(normalData);
        var indexBytes = new byte[indices.Count * sizeof(ushort)];
        for (var i = 0; i < indices.Count; i++) BitConverter.GetBytes(indices[i]).CopyTo(indexBytes, i * 2);

        // Every accessor's data must start on a multiple of its component size, and the BIN chunk
        // itself is padded to four by GlbBinary.
        var buffer = new List<byte>();
        var positionOffset = buffer.Count;
        buffer.AddRange(positionBytes);
        var normalOffset = buffer.Count;
        buffer.AddRange(normalBytes);
        var indexOffset = buffer.Count;
        buffer.AddRange(indexBytes);

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "paradise new" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0, ["name"] = "Cube" }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "Cube",
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = 0, ["NORMAL"] = 1 },
                    ["indices"] = 2,
                    ["mode"] = 4,
                }),
            }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = buffer.Count }),
            ["bufferViews"] = new JsonArray(
                View(positionOffset, positionBytes.Length, 34962),
                View(normalOffset, normalBytes.Length, 34962),
                View(indexOffset, indexBytes.Length, 34963)),
            ["accessors"] = new JsonArray(
                // POSITION carries min/max because the spec requires it, and because every
                // bounds-driven consumer (culling, the migration checker) reads exactly this.
                new JsonObject
                {
                    ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = positions.Count / 3, ["type"] = "VEC3",
                    ["min"] = new JsonArray(-0.5, -0.5, -0.5), ["max"] = new JsonArray(0.5, 0.5, 0.5),
                },
                new JsonObject { ["bufferView"] = 1, ["componentType"] = 5126, ["count"] = normalData.Count / 3, ["type"] = "VEC3" },
                new JsonObject { ["bufferView"] = 2, ["componentType"] = 5123, ["count"] = indices.Count, ["type"] = "SCALAR" }),
        };

        return GlbBinary.Write(gltf, buffer.ToArray());

        static JsonObject View(int offset, int length, int target) => new()
        {
            ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length, ["target"] = target,
        };
    }

    private static byte[] Floats(List<float> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        for (var i = 0; i < values.Count; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, i * sizeof(float));
        return bytes;
    }

    // ---- writing --------------------------------------------------------------------------

    private static void WriteDocument(
        IFileSystem fileSystem, UPath assets, string relative, string text, List<ScaffoldedFile> written, string description)
    {
        var path = assets / relative;
        Write(fileSystem, path, text, written, description);
        Sidecar(fileSystem, path, written);
    }

    private static void WriteBinary(
        IFileSystem fileSystem, UPath assets, string relative, byte[] bytes, List<ScaffoldedFile> written, string description)
    {
        var path = assets / relative;
        CreateParent(fileSystem, path);
        fileSystem.WriteAllBytes(path, bytes);
        written.Add(new ScaffoldedFile(path, description));
        Sidecar(fileSystem, path, written);
    }

    private static void Write(IFileSystem fileSystem, UPath path, string text, List<ScaffoldedFile> written, string description)
    {
        CreateParent(fileSystem, path);
        // UTF-8 with no BOM, matching every other writer in the pipeline.
        fileSystem.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
        written.Add(new ScaffoldedFile(path, description));
    }

    /// <summary>Mints the sidecar every asset must have.</summary>
    private static void Sidecar(IFileSystem fileSystem, UPath asset, List<ScaffoldedFile> written)
    {
        var path = SidecarMeta.PathFor(asset);
        SidecarMeta.Mint().Save(fileSystem, path);
        written.Add(new ScaffoldedFile(path, "identity"));
    }

    private static AssetReference Reference(IFileSystem fileSystem, UPath assets, string relative)
        => new(SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(assets / relative)).Guid, relative);

    private static void CreateParent(IFileSystem fileSystem, UPath path)
    {
        var directory = path.GetDirectory();
        if (!directory.IsNull && !fileSystem.DirectoryExists(directory)) fileSystem.CreateDirectory(directory);
    }
}
