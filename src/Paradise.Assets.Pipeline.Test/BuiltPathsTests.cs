using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A built document names assets where the BUILD put them, so the runtime opens a path and never
/// derives one: a texture reference bakes to its KTX2, prefabs to the profile's extension, a mesh, a
/// material and a clip document to their own names. A GLB ships nothing, so a reference to one is a
/// build error naming the mesh document to reference instead. Each
/// answer is the referenced asset's own importer's (<see cref="IAssetImporter.BuiltPath"/>).
/// </summary>
public class BuiltPathsTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task every_kind_of_reference_bakes_to_the_path_the_build_writes()
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        using var _ = fileSystem;
        foreach (var directory in new[] { "/game/assets/models", "/game/assets/materials", "/game/assets/prefabs", "/game/assets/levels" })
        {
            fileSystem.CreateDirectory(directory);
        }

        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        var fire = SidecarMeta.Load(fileSystem, "/game/assets/textures/fire.png.meta").Guid;

        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", CrateGlb());
        var crate = ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb");
        var mesh = Document(fileSystem, "/game/assets/models/crate.mesh", new MeshReferenceDocument(new AssetReference(crate, "models/crate.glb"), MeshSlot.Mesh));
        var clip = Document(fileSystem, "/game/assets/models/crate.Walk.anim", new MeshReferenceDocument(new AssetReference(crate, "models/crate.glb"), MeshSlot.Clip, "Walk", 0));
        var glbMeta = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");
        GlbImportSettings.WriteExtraction(glbMeta, new GlbExtraction(null, new AssetReference(mesh, "models/crate.mesh"), null, [], [], []));
        glbMeta.Save(fileSystem, "/game/assets/models/crate.glb.meta");

        fileSystem.WriteAllText("/game/assets/materials/rust.material",
            $"Name = \"rust\"\nBaseColorTexture = {{ guid = \"{DocumentGuid.Format(fire)}\", path = \"textures/fire.png\" }}\n");
        var rust = ProjectVerifierTests.Mint(fileSystem, "/game/assets/materials/rust.material");

        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/prefabs/prop.prefab");
        var prop = SidecarMeta.Load(fileSystem, "/game/assets/prefabs/prop.prefab.meta").Guid;

        var scene = new PrefabDocument();
        var thing = PrefabObject.WithMeta(Guid.NewGuid(), "thing");
        thing.Components.Add(new PrefabComponent(Guid.NewGuid(), "Game.Draws", new CanonicalTomlTable
        {
            { "Mesh", AssetReferenceCodec.Write(new AssetReference(mesh, "models/crate.mesh")) },
            { "Clip", AssetReferenceCodec.Write(new AssetReference(clip, "models/crate.Walk.anim")) },
            { "Material", AssetReferenceCodec.Write(new AssetReference(rust, "materials/rust.material")) },
            { "Texture", AssetReferenceCodec.Write(new AssetReference(fire, "textures/fire.png")) },
            { "Prop", AssetReferenceCodec.Write(new AssetReference(prop, "prefabs/prop.prefab")) },
        }));
        scene.Objects.Add(thing);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/levels/scene.prefab", scene);
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, "/game/assets/levels/scene.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Errors).IsEmpty();
        var baked = fileSystem.ReadAllText("/game/build/levels/scene.toml");
        await Assert.That(baked).Contains("Mesh = \"models/crate.mesh\"");
        await Assert.That(baked).Contains("Clip = \"models/crate.Walk.anim\"");
        await Assert.That(baked).Contains("Material = \"materials/rust.material\"");
        await Assert.That(baked).Contains("Texture = \"textures/fire.ktx2\"");
        await Assert.That(baked).Contains("Prop = \"prefabs/prop.toml\"");
        await Assert.That(fileSystem.ReadAllText("/game/build/materials/rust.material")).Contains("BaseColorTexture = \"textures/fire.ktx2\"");
        // Every path a built document names is a file the build wrote.
        foreach (var built in new[] { "models/crate.mesh", "models/crate.Walk.anim", "materials/rust.material", "textures/fire.ktx2", "prefabs/prop.toml" })
        {
            await Assert.That(fileSystem.FileExists("/game/build/" + built)).IsTrue();
        }
    }

    [Test]
    public async Task a_glb_reference_is_a_build_error_naming_the_watcher_when_no_mesh_document_exists()
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        using var _ = fileSystem;
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.CreateDirectory("/game/assets/levels");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", CrateGlb());
        var crate = ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb");

        var scene = new PrefabDocument();
        var thing = PrefabObject.WithMeta(Guid.NewGuid(), "thing");
        thing.Components.Add(new PrefabComponent(Guid.NewGuid(), "Game.Draws", new CanonicalTomlTable
        {
            { "Mesh", AssetReferenceCodec.Write(new AssetReference(crate, "models/crate.glb")) },
        }));
        scene.Objects.Add(thing);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/levels/scene.prefab", scene);
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, "/game/assets/levels/scene.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Single()).Contains("levels/scene.prefab");
        await Assert.That(result.Errors.Single()).Contains("ships nothing");
        await Assert.That(result.Errors.Single()).Contains("paradise assets watch");
    }

    [Test]
    public async Task a_glb_reference_is_a_build_error_naming_the_mesh_document_to_reference_instead()
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        using var _ = fileSystem;
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.CreateDirectory("/game/assets/levels");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", CrateGlb());
        var crate = ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb");
        var mesh = Document(fileSystem, "/game/assets/models/crate.mesh", new MeshReferenceDocument(new AssetReference(crate, "models/crate.glb"), MeshSlot.Mesh));
        var glbMeta = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");
        GlbImportSettings.WriteExtraction(glbMeta, new GlbExtraction(null, new AssetReference(mesh, "models/crate.mesh"), null, [], [], []));
        glbMeta.Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var scene = new PrefabDocument();
        var thing = PrefabObject.WithMeta(Guid.NewGuid(), "thing");
        thing.Components.Add(new PrefabComponent(Guid.NewGuid(), "Game.Draws", new CanonicalTomlTable
        {
            { "Mesh", AssetReferenceCodec.Write(new AssetReference(crate, "models/crate.glb")) },
        }));
        scene.Objects.Add(thing);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/levels/scene.prefab", scene);
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, "/game/assets/levels/scene.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Single()).Contains("ships nothing");
        await Assert.That(result.Errors.Single()).Contains("models/crate.mesh");
        await Assert.That(result.Errors.Single()).Contains(DocumentGuid.Format(mesh));
    }

    private static Guid Document(Zio.FileSystems.MemoryFileSystem fileSystem, UPath path, MeshReferenceDocument document)
    {
        fileSystem.WriteAllBytes(path, document.WriteBytes());
        return ProjectVerifierTests.Mint(fileSystem, path);
    }

    private static byte[] CrateGlb()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var node = b.AddNode(mesh: b.AddMesh(GlbTestBuilder.Primitive(position)), name: "Crate");
        var times = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var values = b.AddFloatAccessor([0f, 0f, 0f, 0f, 2f, 0f], "VEC3");
        b.AddAnimation("Walk", (node, "translation", times, values, null));
        b.SetSceneRoots(node);
        return b.Build();
    }
}
