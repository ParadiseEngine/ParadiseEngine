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

    [Test]
    public async Task a_failure_caused_by_a_file_the_import_read_is_retried_once_that_file_changes()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        var mtl = project.Layout.Assets / "models/crate.mtl";
        project.FileSystem.WriteAllText(mtl, "broken\n");
        // Lists what it read before failing, as the conversion script does.
        project.UseBlender(CrateGlb(0.0), """
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "Blender 4.4.0"; exit 0; fi
            here=$(dirname "$0")
            while [ "$1" != "--" ]; do shift; done
            shift
            echo run >> "$here/runs"
            echo '["crate.mtl"]' > "$3"
            if grep -q broken "$(dirname "$1")/crate.mtl"; then echo "crate.mtl is malformed" >&2; exit 1; fi
            cp "$here/export.glb" "$2"
            """);

        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>().WithMessageContaining("crate.mtl is malformed");
        // Remembered while nothing it read changed: the next document naming the source does not run Blender again.
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>().WithMessageContaining("crate.mtl is malformed");
        await Assert.That(project.BlenderRuns).IsEqualTo(1);

        project.FileSystem.WriteAllText(mtl, "newmtl wood\n");

        await Assert.That(ModelSource.ReadGlb(project.FileSystem, project.Blend)).IsNotEmpty();
        await Assert.That(project.BlenderRuns).IsEqualTo(2);
    }

    [Test]
    public async Task an_unreadable_converted_glb_is_no_conversion_at_all()
    {
        using var project = new Project();
        // A directory where the GLB belongs: reading it is denied, not missing.
        project.FileSystem.CreateDirectory(ModelSource.ConvertedPath(project.Layout, project.Blend));

        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>()
            .WithMessageContaining(BlenderModelConverter.BlenderPathEnvironmentVariable);
    }

    [Test]
    public async Task a_blend_with_asset_collections_extracts_one_model_per_asset()
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), ("Lamp_Short", 0.0), ("Lamp_Tall", 0.5));

        var result = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        await Assert.That(result.Errors).IsEmpty();
        var source = new Paradise.Authoring.AssetReference(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta").Guid, "models/crate.blend");
        foreach (var asset in new[] { "Lamp_Short", "Lamp_Tall" })
        {
            // Each asset is a model of its own, named by it: its documents, its materials and its prefab seed.
            var mesh = MeshReferenceDocument.Load(project.FileSystem, project.Layout.Assets / $"models/{asset}.mesh");
            await Assert.That(mesh.Source).IsEqualTo(source);
            await Assert.That(mesh.Asset).IsEqualTo(asset);
            await Assert.That(project.FileSystem.FileExists(project.Layout.Assets / $"models/{asset}.wood.material")).IsTrue();
            await Assert.That(project.FileSystem.ReadAllText(project.Layout.Assets / $"models/{asset}.prefab")).Contains($"models/{asset}.mesh");
        }

        // The file as a whole is no model: nothing is extracted for it, and reading it says why.
        await Assert.That(project.FileSystem.FileExists(project.Layout.Assets / "models/crate.mesh")).IsFalse();
        await Assert.That(project.FileSystem.FileExists(project.Layout.Assets / "models/crate.prefab")).IsFalse();
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend)).Throws<InvalidDataException>().WithMessageContaining("holds asset collections 'Lamp_Short', 'Lamp_Tall'");

        var parts = ExtractionRecord.Read(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta")).Parts;
        await Assert.That(string.Join(", ", parts.Where(part => part.Kind == ExtractKind.Meshes).Select(part => $"{part.Asset}: {part.Reference.Path}")))
            .IsEqualTo("Lamp_Short: models/Lamp_Short.mesh, Lamp_Tall: models/Lamp_Tall.mesh");
        await Assert.That(string.Join(", ", parts.Where(part => part.Kind == ExtractKind.Materials).Select(part => part.Asset))).IsEqualTo("Lamp_Short, Lamp_Tall");
        await Assert.That(ProjectVerifier.Verify(project.FileSystem, project.Layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();

        var again = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);
        await Assert.That(again.Errors).IsEmpty();
        await Assert.That(again.Written).IsEmpty();
    }

    [Test]
    public async Task a_renamed_asset_collection_leaves_its_documents_reported_not_reminted()
    {
        using var project = new Project();
        project.SeedAssets(Sha256(s_blend), ("Lamp_Short", 0.0), ("Lamp_Tall", 0.0));
        await Assert.That(AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend).Errors).IsEmpty();
        var tall = project.Layout.Assets / "models/Lamp_Tall.mesh";
        var identity = SidecarMeta.Load(project.FileSystem, tall + ".meta").Guid;

        byte[] resaved = [.. s_blend, 1];
        project.FileSystem.WriteAllBytes(project.Blend, resaved);
        project.SeedAssets(Sha256(resaved), ("Lamp_Short", 0.0), ("Lamp_Big", 0.0));

        var result = AssetExtractor.Extract(project.FileSystem, project.Layout, project.Blend);

        // The renamed collection is a new model; the old name's documents stay under their
        // identity and are named, not quietly handed to the new one.
        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Warnings.Single()).Contains("asset collection 'Lamp_Tall'").And.Contains("models/Lamp_Tall.mesh");
        await Assert.That(MeshReferenceDocument.Load(project.FileSystem, project.Layout.Assets / "models/Lamp_Big.mesh").Asset).IsEqualTo("Lamp_Big");
        await Assert.That(SidecarMeta.Load(project.FileSystem, tall + ".meta").Guid).IsEqualTo(identity);
        await Assert.That(MeshReferenceDocument.Load(project.FileSystem, tall).Asset).IsEqualTo("Lamp_Tall");
        await Assert.That(ExtractionRecord.Read(SidecarMeta.Load(project.FileSystem, project.Blend + ".meta")).Parts.Any(part => part.Asset == "Lamp_Tall")).IsFalse();

        var findings = ProjectVerifier.Verify(project.FileSystem, project.Layout).Where(f => f.Severity == VerifySeverity.Error).ToList();
        await Assert.That(findings.Single(f => f.Path == tall).Message).Contains("has no asset collection 'Lamp_Tall'");
    }

    [Test]
    public async Task one_conversion_stores_every_asset_and_drops_the_whole_file_glb_it_replaces()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the stand-in Blender is a shell script");

        using var project = new Project();
        project.Seed(Sha256([.. s_blend, 9]));
        project.UseBlender(CrateGlb(0.0), """
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "Blender 4.4.0"; exit 0; fi
            here=$(dirname "$0")
            while [ "$1" != "--" ]; do shift; done
            shift
            echo run >> "$here/runs"
            echo '[]' > "$3"
            cp "$here/export.glb" "$4/Lamp_B.glb"
            cp "$here/export.glb" "$4/Lamp_A.glb"
            """);

        await Assert.That(ModelSource.Assets(project.FileSystem, project.Blend)).IsEquivalentTo(["Lamp_A", "Lamp_B"], CollectionOrdering.Matching);
        await Assert.That(ModelSource.ReadGlb(project.FileSystem, project.Blend, asset: "Lamp_B")).IsNotEmpty();
        await Assert.That(() => ModelSource.ReadGlb(project.FileSystem, project.Blend, asset: "Lamp_C")).Throws<InvalidDataException>().WithMessageContaining("has no asset collection 'Lamp_C'");
        await Assert.That(project.BlenderRuns).IsEqualTo(1);

        // Each asset's GLB is stamped as one of the file's assets; the stale whole-file GLB is gone.
        await Assert.That(project.FileSystem.FileExists(ModelSource.ConvertedPath(project.Layout, project.Blend))).IsFalse();
        foreach (var asset in new[] { "Lamp_A", "Lamp_B" })
        {
            var stored = project.FileSystem.ReadAllBytes(ModelSource.ConvertedPath(project.Layout, project.Blend, asset));
            await Assert.That(BlenderModelConverter.IsCurrent(stored, Sha256(s_blend), "Blender 4.4.0", _ => null, asset)).IsTrue();
            await Assert.That(BlenderModelConverter.StampedAssets(stored)).IsEquivalentTo(["Lamp_A", "Lamp_B"], CollectionOrdering.Matching);
        }
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

    /// <summary>A project on disk, the only kind whose conversions persist, with Blender out of reach for its lifetime unless <see cref="UseBlender"/> stands one in.</summary>
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

        /// <summary>A conversion of a <c>.blend</c> with asset collections as Blender would have left it: one crate GLB per asset, and nothing else.</summary>
        public void SeedAssets(string sourceSha256, params (string Asset, double Metallic)[] assets)
        {
            var directory = ModelSource.ConvertedDirectory(Layout, Blend);
            if (FileSystem.DirectoryExists(directory)) FileSystem.DeleteDirectory(directory, isRecursive: true);
            FileSystem.CreateDirectory(directory);
            string[] names = [.. assets.Select(asset => asset.Asset)];
            foreach (var (asset, metallic) in assets)
            {
                FileSystem.WriteAllBytes(ModelSource.ConvertedPath(Layout, Blend, asset), BlenderModelConverter.Stamp(
                    CrateGlb(metallic), new BlenderModelConverter.SourceStamp(sourceSha256, BlenderModelConverter.ConverterVersion, "Blender 0.0.0", [], asset, names)));
            }
        }

        /// <summary>Runs <paramref name="script"/> as Blender for the project's lifetime, beside the <paramref name="export"/> it may copy out and the <c>runs</c> file it counts itself in.</summary>
        public void UseBlender(byte[] export, string script)
        {
            var blender = Path.Combine(_root, "blender");
            File.WriteAllText(blender, script + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(blender, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllBytes(Path.Combine(_root, "export.glb"), export);
            Environment.SetEnvironmentVariable(BlenderModelConverter.BlenderPathEnvironmentVariable, blender);
        }

        public int BlenderRuns => File.Exists(Path.Combine(_root, "runs")) ? File.ReadAllLines(Path.Combine(_root, "runs")).Length : 0;

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
