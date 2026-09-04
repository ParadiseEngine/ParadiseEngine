using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// `extract` turns a GLB into authored assets once and keeps them in step afterwards: what it
/// writes, what it never overwrites, how it tells a re-export from an edit, and the prefab that
/// wires the result together.
/// </summary>
public class AssetExtractorTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private const string Glb = "/game/assets/models/crate.glb";

    private static readonly byte[] s_png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private const string Schema = """
        {"version":3,"components":[
          {"id":"edee8bd8-9321-47db-819d-9bdadf010be4","type":"Game.StaticMesh","displayName":"Mesh","fields":[{"name":"Mesh","type":"string","authoredBy":"mesh"}]},
          {"id":"195846ac-d5e5-49a2-8c98-62ac1914c000","type":"Game.SkinnedMesh","displayName":"Skinned","fields":[{"name":"Mesh","type":"string","authoredBy":"mesh"}]}
        ]}
        """;

    /// <summary>A crate: one embedded PNG, two materials (the first samples it), one clip on the mesh node.</summary>
    private static byte[] CrateGlb(float x = 0f)
    {
        var b = new GlbTestBuilder();
        var image = b.AddImage(s_png, "image/png");
        var texture = b.AddTexture(source: image);
        b.AddMaterial(new JsonObject { ["name"] = "wood", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = texture }, ["metallicFactor"] = 0.0 } });
        b.AddMaterial(new JsonObject { ["name"] = "metal", ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = 1.0 }, ["alphaMode"] = "MASK", ["alphaCutoff"] = 0.4 });
        var position = b.AddFloatAccessor([x, 0f, 0f, x + 1f, 0f, 0f, x, 1f, 0f], "VEC3");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, material: 0), GlbTestBuilder.Primitive(position, material: 1));
        var node = b.AddNode(mesh: mesh, name: "Crate");
        var times = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var values = b.AddFloatAccessor([0f, 0f, 0f, 0f, 2f, 0f], "VEC3");
        b.AddAnimation("Bob", (node, "translation", times, values, null));
        b.SetSceneRoots(node);
        return b.Build();
    }

    private static MemoryFileSystem Project(byte[]? glb = null, string? manifest = null)
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        if (manifest is not null) fileSystem.WriteAllText("/game/assets/project.toml", manifest);
        fileSystem.CreateDirectory("/game/.editor");
        fileSystem.WriteAllText("/game/.editor/authoring-schema.json", Schema);
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.WriteAllBytes(Glb, glb ?? CrateGlb());
        ProjectVerifierTests.Mint(fileSystem, Glb);
        return fileSystem;
    }

    [Test]
    public async Task everything_the_glb_embeds_becomes_an_authored_asset_beside_it()
    {
        using var fileSystem = Project();

        var result = AssetExtractor.Extract(fileSystem, s_layout, Glb);

        await Assert.That(result.Errors).IsEmpty();
        foreach (var expected in new[] { "crate.mesh", "crate.skeleton", "crate.Bob.anim", "crate.wood.material", "crate.metal.material", "crate_0.png", "crate.prefab" })
        {
            await Assert.That(fileSystem.FileExists("/game/assets/models/" + expected)).IsTrue().Because(expected);
            await Assert.That(fileSystem.FileExists("/game/assets/models/" + expected + ".meta")).IsTrue().Because(expected + ".meta");
        }

        // The image left the container: the file IS the texture now, and the GLB points at it.
        var images = MeshContainer.Read(Glb, fileSystem.ReadAllBytes(Glb));
        await Assert.That(images.Count).IsEqualTo(1);
        await Assert.That(images[0].Uri).IsEqualTo("crate_0.png");
        await Assert.That(fileSystem.ReadAllBytes("/game/assets/models/crate_0.png")).IsEquivalentTo(s_png);

        // The material samples the extracted texture by identity, and knows nothing of the GLB.
        var wood = MaterialDocument.Load(fileSystem, "/game/assets/models/crate.wood.material");
        var png = SidecarMeta.Load(fileSystem, "/game/assets/models/crate_0.png.meta").Guid;
        await Assert.That(MaterialDocument.References(wood).Single().Reference.Guid).IsEqualTo(png);
        await Assert.That(wood.Value("MetallicFactor")).IsEqualTo(0.0);
        var metal = MaterialDocument.Load(fileSystem, "/game/assets/models/crate.metal.material");
        await Assert.That(metal.Value("AlphaMode")).IsEqualTo("Mask");
        await Assert.That(MaterialDocument.References(metal)).IsEmpty();

        // The tree verifies clean: the GLB is extracted, every reference resolves.
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task the_prefab_names_the_mesh_component_and_binds_slots_in_primitive_order()
    {
        using var fileSystem = Project();

        AssetExtractor.Extract(fileSystem, s_layout, Glb);

        var prefab = PrefabDocumentSerializer.Load(fileSystem, "/game/assets/models/crate.prefab");
        var root = prefab.Root;
        await Assert.That(root.Name).IsEqualTo("crate");
        await Assert.That(root.Meta!.Data.Value(GlbExtraction.GeneratedFrom)).IsEqualTo(DocumentGuid.Format(SidecarMeta.Load(fileSystem, Glb + ".meta").Guid));
        var meshComponent = root.Component(Guid.Parse("edee8bd8-9321-47db-819d-9bdadf010be4"))!;
        await Assert.That(meshComponent.Type).IsEqualTo("Game.StaticMesh");
        var meshReference = (CanonicalInlineTable)meshComponent.Data.Value("Mesh")!;
        await Assert.That(meshReference.Value("path")).IsEqualTo("models/crate.mesh");
        var slots = (IReadOnlyList<object>)root.Component(GlbExtraction.MaterialsComponentId)!.Data.Value("Slots")!;
        await Assert.That(slots.Count).IsEqualTo(2);
        await Assert.That(((CanonicalInlineTable)slots[0]).Value("path")).IsEqualTo("models/crate.wood.material");
        await Assert.That(((CanonicalInlineTable)slots[1]).Value("path")).IsEqualTo("models/crate.metal.material");
    }

    [Test]
    public async Task a_second_run_writes_nothing_and_the_record_survives()
    {
        using var fileSystem = Project();
        AssetExtractor.Extract(fileSystem, s_layout, Glb);
        var before = fileSystem.ReadAllText(Glb + ".meta");

        var again = AssetExtractor.Extract(fileSystem, s_layout, Glb);

        await Assert.That(again.Errors).IsEmpty();
        await Assert.That(again.Written).IsEmpty();
        await Assert.That(again.Kept).Contains("models/crate.prefab");
        await Assert.That(fileSystem.ReadAllText(Glb + ".meta")).IsEqualTo(before);
        await Assert.That(GlbImportSettings.ReadExtraction(SidecarMeta.Load(fileSystem, Glb + ".meta")).Extracted).IsTrue();
    }

    [Test]
    public async Task a_re_exported_glb_re_extracts_the_mesh_and_leaves_the_prefab_alone()
    {
        using var fileSystem = Project();
        AssetExtractor.Extract(fileSystem, s_layout, Glb);
        var prefab = fileSystem.ReadAllText("/game/assets/models/crate.prefab");
        fileSystem.WriteAllBytes(Glb, CrateGlb(x: 5f));

        var result = AssetExtractor.Extract(fileSystem, s_layout, Glb);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Written.Any(w => w.StartsWith("models/crate.mesh") && w.Contains("re-extracted"))).IsTrue();
        await Assert.That(Paradise.Assets.Mesh.MeshBlobFormat.Read(fileSystem.ReadAllBytes("/game/assets/models/crate.mesh")).Vertices[0]).IsEqualTo(5f);
        await Assert.That(fileSystem.ReadAllText("/game/assets/models/crate.prefab")).IsEqualTo(prefab);
    }

    [Test]
    public async Task an_edited_blob_is_a_warning_and_both_sides_changed_is_a_conflict_the_flags_resolve()
    {
        using var fileSystem = Project();
        AssetExtractor.Extract(fileSystem, s_layout, Glb);
        fileSystem.WriteAllBytes("/game/assets/models/crate.Bob.anim", [1, 2, 3]);

        var edited = AssetExtractor.Extract(fileSystem, s_layout, Glb);
        await Assert.That(edited.Errors).IsEmpty();
        await Assert.That(edited.Warnings.Any(w => w.Contains("crate.Bob.anim") && w.Contains("cannot be written back"))).IsTrue();
        await Assert.That(fileSystem.ReadAllBytes("/game/assets/models/crate.Bob.anim")).IsEquivalentTo(new byte[] { 1, 2, 3 });

        // Both sides of the MESH move: the GLB's geometry, and the extracted blob under it.
        fileSystem.WriteAllBytes(Glb, CrateGlb(x: 5f));
        fileSystem.WriteAllBytes("/game/assets/models/crate.mesh", [4, 5, 6]);
        var conflict = AssetExtractor.Extract(fileSystem, s_layout, Glb);
        await Assert.That(conflict.Succeeded).IsFalse();
        await Assert.That(conflict.Errors.Any(e => e.Contains("crate.mesh") && e.Contains("--take-glb"))).IsTrue();
        await Assert.That(fileSystem.ReadAllBytes("/game/assets/models/crate.mesh")).IsEquivalentTo(new byte[] { 4, 5, 6 });

        var resolved = AssetExtractor.Extract(fileSystem, s_layout, Glb, resolution: ConflictResolution.TakeGlb);
        await Assert.That(resolved.Succeeded).IsTrue();
        await Assert.That(Paradise.Assets.Mesh.MeshBlobFormat.Read(fileSystem.ReadAllBytes("/game/assets/models/crate.mesh")).Vertices[0]).IsEqualTo(5f);
        // The edited clip is still the author's, still diverged, still said so.
        await Assert.That(resolved.Warnings.Any(w => w.Contains("crate.Bob.anim"))).IsTrue();
    }

    [Test]
    public async Task an_edited_material_document_is_written_back_into_the_glb()
    {
        using var fileSystem = Project();
        AssetExtractor.Extract(fileSystem, s_layout, Glb);
        ProjectVerifierTests.WriteCarried(fileSystem, "/game/assets/textures/rust.png", "png");
        var rust = SidecarMeta.Load(fileSystem, "/game/assets/textures/rust.png.meta").Guid;
        var metal = MaterialDocument.Load(fileSystem, "/game/assets/models/crate.metal.material");
        var edited = new CanonicalTomlTable();
        foreach (var (key, value) in metal)
        {
            edited.Add(key, key switch
            {
                "MetallicFactor" => 0.25,
                "BaseColorTexture" => AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(rust, "textures/rust.png")),
                _ => value,
            });
        }

        edited.Add("MaterialKind", "lava");   // Paradise-only: the document's alone, never a divergence
        fileSystem.WriteAllBytes("/game/assets/models/crate.metal.material", CanonicalTomlWriter.WriteBytes(edited));

        var result = AssetExtractor.Extract(fileSystem, s_layout, Glb);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Written.Any(w => w.Contains("crate.glb") && w.Contains("written back"))).IsTrue();
        var asset = Paradise.Assets.Gltf.GltfSceneReader.ReadGeometry(fileSystem.ReadAllBytes(Glb));
        await Assert.That(asset.Materials[1].MetallicFactor).IsEqualTo(0.25f);
        await Assert.That(asset.Materials[1].BaseColorImage).IsGreaterThanOrEqualTo(0);
        var images = MeshContainer.Read(Glb, fileSystem.ReadAllBytes(Glb));
        await Assert.That(images.Any(i => i.Uri == "../textures/rust.png")).IsTrue();
        // Settled: the next run has nothing to do, and the Paradise-only field is still there.
        var again = AssetExtractor.Extract(fileSystem, s_layout, Glb);
        await Assert.That(again.Written).IsEmpty();
        await Assert.That(MaterialDocument.Load(fileSystem, "/game/assets/models/crate.metal.material").Value("MaterialKind")).IsEqualTo("lava");
    }

    [Test]
    public async Task a_re_exported_material_updates_the_document_and_keeps_its_paradise_only_fields()
    {
        using var fileSystem = Project();
        AssetExtractor.Extract(fileSystem, s_layout, Glb);
        var metal = MaterialDocument.Load(fileSystem, "/game/assets/models/crate.metal.material");
        var withKind = new CanonicalTomlTable();
        foreach (var (key, value) in metal) withKind.Add(key, value);
        withKind.Add("MaterialKind", "lava");
        fileSystem.WriteAllBytes("/game/assets/models/crate.metal.material", CanonicalTomlWriter.WriteBytes(withKind));
        AssetExtractor.Extract(fileSystem, s_layout, Glb);   // settles the Paradise-only edit as no divergence

        // The artist re-exports with a different roughness on 'metal'.
        var glb = GlbMaterialWriter.Write(fileSystem.ReadAllBytes(Glb), "models/crate.glb", 1, new CanonicalTomlTable { { "RoughnessFactor", 0.1 } });
        fileSystem.WriteAllBytes(Glb, glb);

        var result = AssetExtractor.Extract(fileSystem, s_layout, Glb);

        await Assert.That(result.Errors).IsEmpty();
        var updated = MaterialDocument.Load(fileSystem, "/game/assets/models/crate.metal.material");
        await Assert.That(updated.Value("RoughnessFactor")).IsEqualTo(0.1);
        await Assert.That(updated.Value("MaterialKind")).IsEqualTo("lava");
    }

    [Test]
    public async Task the_project_default_directory_and_the_sidecar_override_place_the_output()
    {
        using var fileSystem = Project(manifest: "name = \"x\"\nschema_version = 1\n\n[extract]\ndirectory = \"extracted\"\n");

        var byProject = AssetExtractor.Extract(fileSystem, s_layout, Glb);
        await Assert.That(byProject.Errors).IsEmpty();
        await Assert.That(fileSystem.FileExists("/game/assets/extracted/crate.mesh")).IsTrue();

        using var overridden = Project();
        var meta = SidecarMeta.Load(overridden, Glb + ".meta");
        meta.SetSetting(GlbImportSettings.Domain, new CanonicalTomlTable { { GlbImportSettings.ExtractKey, "models/crate" } });
        meta.Save(overridden, Glb + ".meta");

        var bySidecar = AssetExtractor.Extract(overridden, s_layout, Glb);
        await Assert.That(bySidecar.Errors).IsEmpty();
        await Assert.That(overridden.FileExists("/game/assets/models/crate/crate.mesh")).IsTrue();
    }

    [Test]
    public async Task an_unextracted_glb_is_a_verify_warning_naming_the_verb()
    {
        using var fileSystem = Project();

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("paradise assets extract");
    }

    [Test]
    public async Task the_extracted_tree_builds_without_opening_the_glb()
    {
        using var fileSystem = Project();
        AssetExtractor.Extract(fileSystem, s_layout, Glb);

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.mesh")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.Bob.anim")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.wood.toml")).IsTrue();
        await Assert.That(fileSystem.ReadAllText("/game/build/models/crate.wood.toml")).Contains("BaseColorTexture = \"models/crate_0.ktx2\"");
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsFalse();
    }
}
