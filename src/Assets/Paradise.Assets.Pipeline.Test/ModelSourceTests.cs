using System.Security.Cryptography;
using System.Text.Json.Nodes;

using TUnit.Assertions.Enums;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A <c>.blend</c>, <c>.obj</c> or any other converted format is a model source read through the
/// GLB Blender converted it to: extraction treats it like a GLB and never writes into it, and a
/// conversion that cannot be made current says what it needs. Blender is kept out by pointing
/// PARADISE_BLENDER_PATH at nothing, which is also what a machine without Blender sees.
/// </summary>
[NotInParallel]
public class ModelSourceTests
{
    private static readonly byte[] s_png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static readonly byte[] s_blend = "BLENDER-v500 not a real one, never opened"u8.ToArray();

    private const string Schema = """
        {"version":3,"components":[
          {"id":"edee8bd8-9321-47db-819d-9bdadf010be4","type":"Game.StaticMesh","displayName":"Mesh","fields":[{"name":"Mesh","type":"string","authoredBy":"mesh"}]}
        ]}
        """;

    [Test]
    public async Task a_blend_extracts_through_its_converted_glb_without_being_written()
    {
        using var project = new Project();
        project.Seed(Sha256(s_blend));

        var result = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Errors).IsEmpty();
        var source = SidecarMeta.Load(project.FileSystem, project.Blend + ".meta").Guid;
        var mesh = MeshReferenceDocument.Load(project.FileSystem, project.Layout.Assets / "models/crate.mesh");
        await Assert.That(mesh.Source).IsEqualTo(new Paradise.Authoring.AssetReference(source, "models/crate.blend"));

        // The embedded image became a texture file, and the material binds it by identity even
        // though the source still embeds it.
        await Assert.That(project.FileSystem.ReadAllBytes(project.Layout.Assets / "models/crate_0.png")).IsEquivalentTo(s_png, CollectionOrdering.Matching);
        var png = SidecarMeta.Load(project.FileSystem, project.Layout.Assets / "models/crate_0.png.meta").Guid;
        var wood = MaterialDocument.Load(project.FileSystem, project.Layout.Assets / "models/crate.wood.material");
        await Assert.That(MaterialDocument.References(wood).Single().Reference.Guid).IsEqualTo(png);
        await Assert.That(project.FileSystem.ReadAllText(project.Layout.Assets / "models/crate.prefab")).Contains("models/crate.mesh");

        await Assert.That(project.FileSystem.ReadAllBytes(project.Blend)).IsEquivalentTo(s_blend, CollectionOrdering.Matching);
        await Assert.That(ProjectVerifier.Verify(project.FileSystem, project.Layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();

        var again = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);
        await Assert.That(again.Errors).IsEmpty();
        await Assert.That(again.Written).IsEmpty();
    }

    [Test]
    public async Task an_edited_material_of_a_blend_stands_until_the_source_changes_it()
    {
        using var project = new Project();
        project.Seed(Sha256(s_blend));
        await Assert.That(AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();
        var material = project.Layout.Assets / "models/crate.wood.material";
        project.FileSystem.WriteAllText(material, "Shader = \"toon\"\n" + project.FileSystem.ReadAllText(material).Replace("MetallicFactor = 0.0", "MetallicFactor = 0.5"));

        var kept = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        // Nothing goes back into the .blend, and the edit is settled rather than re-raised.
        await Assert.That(kept.Errors).IsEmpty();
        await Assert.That(project.FileSystem.ReadAllBytes(project.Blend)).IsEquivalentTo(s_blend, CollectionOrdering.Matching);
        await Assert.That(MaterialDocument.Load(project.FileSystem, material).Value("MetallicFactor")).IsEqualTo(0.5);
        var settled = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);
        await Assert.That(settled.Errors).IsEmpty();
        await Assert.That(settled.Written).IsEmpty();

        // The author re-saves the .blend with a new material: the source's values win, and the
        // field glTF cannot express survives.
        byte[] resaved = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, resaved);
        project.Seed(Sha256(resaved), metallic: 0.25);

        var reExtracted = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(reExtracted.Errors).IsEmpty();
        var merged = MaterialDocument.Load(project.FileSystem, material);
        await Assert.That(merged.Value("MetallicFactor")).IsEqualTo(0.25);
        await Assert.That(merged.Value("Shader")).IsEqualTo("toon");
    }

    [Test]
    public async Task a_stale_conversion_without_blender_names_the_setting_that_finds_it()
    {
        using var project = new Project();
        project.Seed(Sha256([.. s_blend, 1]));

        var result = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Single()).Contains("models/crate.blend");
        await Assert.That(result.Errors.Single()).Contains(BlenderModelConverter.BlenderPathEnvironmentVariable);
        await Assert.That(project.FileSystem.FileExists(project.Layout.Assets / "models/crate.mesh")).IsFalse();

        project.FileSystem.DeleteFile(ModelSource.ConvertedPath(project.Layout, project.Blend));
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);
    }

    [Test]
    public async Task a_changed_or_missing_dependency_makes_the_conversion_stale()
    {
        using var project = new Project();
        var mtl = project.Layout.Assets / "models/crate.mtl";
        var png = project.Layout.Assets / "textures/wood.png";
        project.FileSystem.CreateDirectory(png.GetDirectory());
        project.FileSystem.WriteAllText(mtl, "newmtl wood\nmap_Kd ../textures/wood.png\n");
        project.FileSystem.WriteAllBytes(png, s_png);
        project.Seed(Sha256(s_blend), dependencies: [new("crate.mtl", Sha256(project.FileSystem.ReadAllBytes(mtl))), new("../textures/wood.png", Sha256(s_png))]);

        await Assert.That(AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();

        // A re-saved texture is a new input the stored GLB was not made from; with no Blender to
        // remake it, the read fails rather than serving the old pixels.
        project.FileSystem.WriteAllBytes(png, [.. s_png, 9]);
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);

        project.FileSystem.WriteAllBytes(png, s_png);
        await Assert.That(ModelSource.ReadGlb(project.FileSystem, project.Blend)).IsNotEmpty();

        project.FileSystem.DeleteFile(mtl);
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A crate: one embedded PNG sampled by its one material.</summary>
    private static byte[] CrateGlb(double metallic)
    {
        var b = new GlbTestBuilder();
        var image = b.AddImage(s_png, "image/png");
        var texture = b.AddTexture(source: image);
        b.AddMaterial(new JsonObject { ["name"] = "wood", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = texture }, ["metallicFactor"] = metallic } });
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var node = b.AddNode(mesh: b.AddMesh(GlbTestBuilder.Primitive(position, material: 0)), name: "Crate");
        b.SetSceneRoots(node);
        return b.Build();
    }

    /// <summary>A project on disk, the only kind whose conversions persist, with Blender out of reach for its lifetime.</summary>
    private sealed class Project : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"paradise_model_{Guid.NewGuid():N}");
        private readonly string? _blender = Environment.GetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable);

        public Project()
        {
            Environment.SetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable, Path.Combine(_root, "no-blender"));
            Directory.CreateDirectory(Path.Combine(_root, "assets", "models"));
            Directory.CreateDirectory(Path.Combine(_root, ".editor"));
            File.WriteAllText(Path.Combine(_root, "assets", "project.toml"), "name = \"game\"\nschema_version = 1\n");
            File.WriteAllText(Path.Combine(_root, ".editor", "authoring-schema.json"), Schema);

            Layout = new AssetProjectLayout(FileSystem.ConvertPathFromInternal(_root));
            Blend = Layout.Assets / "models/crate.blend";
            FileSystem.WriteAllBytes(Blend, s_blend);
            Mint(Layout.Manifest);
            Mint(Blend);
        }

        public PhysicalFileSystem FileSystem { get; } = new();

        public AssetProjectLayout Layout { get; }

        public UPath Blend { get; }

        /// <summary>A conversion as Blender would have left it, stamped as made from a source with <paramref name="sourceSha256"/> and the <paramref name="dependencies"/> it read.</summary>
        public void Seed(string sourceSha256, double metallic = 0.0, BlenderModelConverter.Dependency[]? dependencies = null)
        {
            var converted = ModelSource.ConvertedPath(Layout, Blend);
            FileSystem.CreateDirectory(converted.GetDirectory());
            FileSystem.WriteAllBytes(converted, BlenderModelConverter.Stamp(
                CrateGlb(metallic), new BlenderModelConverter.SourceStamp(sourceSha256, BlenderModelConverter.ConverterVersion, "Blender 0.0.0", dependencies ?? [])));
        }

        private void Mint(UPath asset)
        {
            var meta = SidecarMeta.Mint();
            meta.Importer = ImporterChain.Claim(AssetImporters.All, new ImportCandidate(FileSystem, Layout, asset, null))?.Name;
            meta.Save(FileSystem, SidecarMeta.PathFor(asset));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable, _blender);
            FileSystem.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
