using System.Text.Json.Nodes;

using TUnit.Assertions.Enums;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A <c>.gltf</c> is the GLB whose buffers live beside it: extraction and the build read it
/// directly, extraction writes back into its JSON, and its <c>.bin</c> is a build input.
/// </summary>
public class GltfSourceTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private const string Gltf = "/game/assets/models/crate.gltf";
    private const string Bin = "/game/assets/models/crate.bin";

    private static readonly byte[] s_png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private const string Schema = """
        {"version":3,"components":[
          {"id":"edee8bd8-9321-47db-819d-9bdadf010be4","type":"Game.StaticMesh","displayName":"Mesh","fields":[{"name":"Mesh","type":"string","authoredBy":"mesh"}]}
        ]}
        """;

    [Test]
    public async Task a_gltf_beside_its_bin_and_png_extracts_and_builds_what_the_same_glb_does()
    {
        const string glb = "/game/assets/models/crate.glb";
        using var glbProject = Project();
        Texture(glbProject, "/game/assets/models/crate.png");
        glbProject.WriteAllBytes(glb, Crate(b => b.AddExternalImage("crate.png")));
        ProjectVerifierTests.Mint(glbProject, glb);

        using var gltfProject = Project();
        Texture(gltfProject, "/game/assets/models/crate.png");
        WriteGltf(gltfProject, Crate(b => b.AddExternalImage("crate.png")));

        var fromGlb = AssetExtractor.Extract(glbProject, s_layout, glb);
        var fromGltf = AssetExtractor.Extract(gltfProject, s_layout, Gltf);

        await Assert.That(fromGlb.Errors).IsEmpty();
        await Assert.That(fromGltf.Errors).IsEmpty();
        await Assert.That(Extracted(gltfProject)).IsEquivalentTo(Extracted(glbProject), CollectionOrdering.Matching);
        await Assert.That(Extracted(gltfProject)).Contains("crate.prefab");
        await Assert.That(MeshReferenceDocument.Load(gltfProject, "/game/assets/models/crate.mesh").Source.Path).IsEqualTo("models/crate.gltf");

        var png = SidecarMeta.Load(gltfProject, "/game/assets/models/crate.png.meta").Guid;
        var wood = MaterialDocument.Load(gltfProject, "/game/assets/models/crate.wood.material");
        await Assert.That(MaterialDocument.References(wood).Single().Reference).IsEqualTo(new Paradise.Authoring.AssetReference(png, "models/crate.png"));

        // Read directly: nothing is converted, and the source is still the JSON the DCC wrote.
        await Assert.That(gltfProject.DirectoryExists("/game/.editor/converted")).IsFalse();
        await Assert.That(GltfFile.TryParse(gltfProject.ReadAllBytes(Gltf), out _)).IsTrue();
        await Assert.That(ProjectVerifier.Verify(gltfProject, s_layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();

        await Assert.That(Build(glbProject).Errors).IsEmpty();
        await Assert.That(Build(gltfProject).Errors).IsEmpty();
        await Assert.That(gltfProject.ReadAllBytes("/game/build/models/crate.mesh"))
            .IsEquivalentTo(glbProject.ReadAllBytes("/game/build/models/crate.mesh"), CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_data_uri_image_becomes_a_texture_file_the_gltf_names()
    {
        using var fileSystem = Project();
        WriteGltf(fileSystem, Crate(b => b.AddRawImage(new JsonObject
        {
            ["uri"] = $"data:image/png;base64,{Convert.ToBase64String(s_png)}",
        })));
        var bin = fileSystem.ReadAllBytes(Bin);

        var result = AssetExtractor.Extract(fileSystem, s_layout, Gltf);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(fileSystem.ReadAllBytes("/game/assets/models/crate_0.png")).IsEquivalentTo(s_png, CollectionOrdering.Matching);

        // Still a .gltf: the image is a relative file now, the buffer is where it was, and the
        // geometry it holds did not change.
        await Assert.That(GltfFile.TryParse(fileSystem.ReadAllBytes(Gltf), out var gltf)).IsTrue();
        await Assert.That((string?)gltf["images"]![0]!["uri"]).IsEqualTo("crate_0.png");
        await Assert.That(gltf["images"]![0]!["bufferView"]).IsNull();
        await Assert.That((string?)gltf["buffers"]![0]!["uri"]).IsEqualTo("crate.bin");
        await Assert.That(fileSystem.ReadAllBytes(Bin)).IsEquivalentTo(bin, CollectionOrdering.Matching);

        var png = SidecarMeta.Load(fileSystem, "/game/assets/models/crate_0.png.meta").Guid;
        var wood = MaterialDocument.Load(fileSystem, "/game/assets/models/crate.wood.material");
        await Assert.That(MaterialDocument.References(wood).Single().Reference.Guid).IsEqualTo(png);
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();
        await Assert.That(Build(fileSystem).Errors).IsEmpty();

        var again = AssetExtractor.Extract(fileSystem, s_layout, Gltf);
        await Assert.That(again.Errors).IsEmpty();
        await Assert.That(again.Written).IsEmpty();
    }

    [Test]
    public async Task a_moved_texture_is_followed_into_the_gltf_uri()
    {
        using var fileSystem = Project();
        Texture(fileSystem, "/game/assets/textures/rust.png");
        WriteGltf(fileSystem, Crate(b => b.AddExternalImage("../textures/rust.png")));
        await Assert.That(AssetExtractor.Extract(fileSystem, s_layout, Gltf).Errors).IsEmpty();

        var moved = AssetMover.Move(fileSystem, s_layout, "/game/assets/textures/rust.png", "/game/assets/textures/metal/rust.png");

        await Assert.That(moved.Rewritten).Contains("models/crate.gltf");
        await Assert.That(GltfFile.TryParse(fileSystem.ReadAllBytes(Gltf), out var gltf)).IsTrue();
        await Assert.That((string?)gltf["images"]![0]!["uri"]).IsEqualTo("../textures/metal/rust.png");
        await Assert.That((string?)gltf["buffers"]![0]!["uri"]).IsEqualTo("crate.bin");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task a_changed_bin_rebuilds_the_mesh()
    {
        using var fileSystem = Project();
        Texture(fileSystem, "/game/assets/models/crate.png");
        WriteGltf(fileSystem, Crate(b => b.AddExternalImage("crate.png")));
        await Assert.That(AssetExtractor.Extract(fileSystem, s_layout, Gltf).Errors).IsEmpty();
        await Assert.That(Build(fileSystem).Errors).IsEmpty();
        await Assert.That(Paradise.Assets.Mesh.MeshBlobFormat.Read(fileSystem.ReadAllBytes("/game/build/models/crate.mesh")).Vertices[0]).IsEqualTo(0f);

        // Only the buffer changes; the .gltf and every document are as they were.
        GlbBinary.TryRead(Crate(b => b.AddExternalImage("crate.png"), x: 5f), out _, out var moved);
        fileSystem.WriteAllBytes(Bin, moved);

        await Assert.That(Build(fileSystem).Errors).IsEmpty();
        await Assert.That(Paradise.Assets.Mesh.MeshBlobFormat.Read(fileSystem.ReadAllBytes("/game/build/models/crate.mesh")).Vertices[0]).IsEqualTo(5f);
    }

    [Test]
    [Arguments("../../outside.bin")]
    [Arguments("/game/outside.bin")]
    [Arguments("file:///game/outside.bin")]
    public async Task a_buffer_uri_outside_assets_is_refused(string uri)
    {
        using var fileSystem = Project();
        WriteGltf(fileSystem, Crate(b => b.AddExternalImage("crate.png")));
        fileSystem.WriteAllBytes("/game/outside.bin", fileSystem.ReadAllBytes(Bin));
        var gltf = JsonNode.Parse(fileSystem.ReadAllText(Gltf))!.AsObject();
        gltf["buffers"]![0]!["uri"] = uri;
        fileSystem.WriteAllText(Gltf, gltf.ToJsonString());

        await Assert.That(() => ModelSource.ReadGlb(fileSystem, Gltf)).Throws<InvalidDataException>().WithMessageContaining("leaves assets/");
    }

    /// <summary>A crate: one triangle whose one material samples the image <paramref name="image"/> adds.</summary>
    private static byte[] Crate(Func<GlbTestBuilder, int> image, float x = 0f)
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([x, 0f, 0f, x + 1f, 0f, 0f, x, 1f, 0f], "VEC3");
        var texture = b.AddTexture(source: image(b));
        b.AddMaterial(new JsonObject { ["name"] = "wood", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = texture } } });
        var node = b.AddNode(mesh: b.AddMesh(GlbTestBuilder.Primitive(position, material: 0)), name: "Crate");
        b.SetSceneRoots(node);
        return b.Build();
    }

    /// <summary>The GLB as a DCC would export it separately: its JSON as <c>crate.gltf</c>, its BIN as <c>crate.bin</c>.</summary>
    private static void WriteGltf(MemoryFileSystem fileSystem, byte[] glb)
    {
        GlbBinary.TryRead(glb, out var gltf, out var bin);
        var buffer = gltf["buffers"]![0]!;
        buffer["uri"] = "crate.bin";
        fileSystem.WriteAllBytes(Bin, bin[..buffer["byteLength"]!.GetValue<int>()]);
        fileSystem.WriteAllText(Gltf, gltf.ToJsonString());
        ProjectVerifierTests.Mint(fileSystem, Gltf);
        ProjectVerifierTests.Mint(fileSystem, Bin);
    }

    private static void Texture(MemoryFileSystem fileSystem, UPath path)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllBytes(path, s_png);
        ProjectVerifierTests.Mint(fileSystem, path);
    }

    /// <summary>What extraction left in <c>models/</c>, the sources and sidecars aside.</summary>
    private static List<string> Extracted(MemoryFileSystem fileSystem)
        => [.. fileSystem.EnumerateFiles("/game/assets/models")
            .Where(path => !SidecarMeta.IsSidecarPath(path) && path.GetExtensionWithDot() is not (".glb" or ".gltf" or ".bin"))
            .Select(path => path.GetName())
            .Order(StringComparer.Ordinal)];

    private static BuildResult Build(MemoryFileSystem fileSystem) => new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

    private static MemoryFileSystem Project()
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/.editor");
        fileSystem.WriteAllText("/game/.editor/authoring-schema.json", Schema);
        return fileSystem;
    }
}
