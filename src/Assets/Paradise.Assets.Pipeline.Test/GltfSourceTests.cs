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
/// directly and never write it, its <c>.bin</c> is a build input, and a moved <c>.bin</c> is found
/// by the identity its sidecar records.
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
    public async Task a_data_uri_image_becomes_a_texture_file_and_the_gltf_is_left_as_it_was()
    {
        using var fileSystem = Project();
        WriteGltf(fileSystem, Crate(b => b.AddRawImage(new JsonObject
        {
            ["uri"] = $"data:image/png;base64,{Convert.ToBase64String(s_png)}",
        })));
        var json = fileSystem.ReadAllBytes(Gltf);
        var bin = fileSystem.ReadAllBytes(Bin);

        var result = AssetExtractor.Extract(fileSystem, s_layout, Gltf);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(fileSystem.ReadAllBytes("/game/assets/models/crate_0.png")).IsEquivalentTo(s_png, CollectionOrdering.Matching);
        await Assert.That(fileSystem.ReadAllBytes(Gltf)).IsEquivalentTo(json, CollectionOrdering.Matching);
        await Assert.That(fileSystem.ReadAllBytes(Bin)).IsEquivalentTo(bin, CollectionOrdering.Matching);

        // The material binds the extracted file by identity, through the record.
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
    public async Task a_moved_texture_is_followed_by_identity_and_the_gltf_is_left_as_it_was()
    {
        using var fileSystem = Project();
        Texture(fileSystem, "/game/assets/textures/rust.png");
        WriteGltf(fileSystem, Crate(b => b.AddExternalImage("../textures/rust.png")));
        await Assert.That(AssetExtractor.Extract(fileSystem, s_layout, Gltf).Errors).IsEmpty();
        var json = fileSystem.ReadAllBytes(Gltf);

        var moved = AssetMover.Move(fileSystem, s_layout, "/game/assets/textures/rust.png", "/game/assets/textures/metal/rust.png");

        await Assert.That(moved.Rewritten).Contains("models/crate.gltf.meta");
        await Assert.That(fileSystem.ReadAllBytes(Gltf)).IsEquivalentTo(json, CollectionOrdering.Matching);
        await Assert.That(MeshReferences.Recorded(fileSystem, Gltf).Single(entry => entry.Slot == "images[0]").Reference.Path).IsEqualTo("textures/metal/rust.png");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
        await Assert.That(Build(fileSystem).Errors).IsEmpty();

        // A re-extract still binds the material to the texture where it is now.
        var rust = SidecarMeta.Load(fileSystem, "/game/assets/textures/metal/rust.png.meta").Guid;
        await Assert.That(AssetExtractor.Extract(fileSystem, s_layout, Gltf).Warnings).IsEmpty();
        var wood = MaterialDocument.Load(fileSystem, "/game/assets/models/crate.wood.material");
        await Assert.That(MaterialDocument.References(wood).Single().Reference.Guid).IsEqualTo(rust);
    }

    [Test]
    public async Task its_bin_is_recorded_by_identity_like_an_image()
    {
        using var fileSystem = ExtractedProject();

        var bin = SidecarMeta.Load(fileSystem, Bin + ".meta").Guid;
        await Assert.That(MeshReferences.Recorded(fileSystem, Gltf))
            .Contains(new MeshReference("buffers[0]", "crate.bin", new Paradise.Authoring.AssetReference(bin, "models/crate.bin")));
    }

    [Test]
    public async Task a_moved_gltf_still_reads_the_files_it_names_by_identity()
    {
        using var fileSystem = ExtractedProject();
        const string moved = "/game/assets/other/crate.gltf";
        var json = fileSystem.ReadAllBytes(Gltf);

        var result = AssetMover.Move(fileSystem, s_layout, Gltf, moved);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Warnings).IsEmpty();
        await Assert.That(fileSystem.ReadAllBytes(moved)).IsEquivalentTo(json, CollectionOrdering.Matching);
        await Assert.That(fileSystem.FileExists(Bin)).IsTrue();
        await Assert.That(ModelSource.ReadGlb(fileSystem, moved, index: Index(fileSystem))).IsNotEmpty();
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
        await Assert.That(Build(fileSystem).Errors).IsEmpty();
    }

    [Test]
    public async Task a_moved_bin_is_followed_by_identity_and_the_gltf_is_left_as_it_was()
    {
        using var fileSystem = ExtractedProject();
        var json = fileSystem.ReadAllBytes(Gltf);

        var result = AssetMover.Move(fileSystem, s_layout, Bin, "/game/assets/buffers/crate.bin");

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Rewritten).IsEquivalentTo(new[] { "models/crate.gltf.meta" }, CollectionOrdering.Matching);
        await Assert.That(fileSystem.ReadAllBytes(Gltf)).IsEquivalentTo(json, CollectionOrdering.Matching);
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
        await Assert.That(Build(fileSystem).Errors).IsEmpty();
    }

    [Test]
    public async Task a_bin_moved_outside_the_tool_still_cooks_through_its_recorded_guid()
    {
        using var fileSystem = ExtractedProject();
        var json = fileSystem.ReadAllBytes(Gltf);
        fileSystem.CreateDirectory("/game/assets/buffers");
        fileSystem.MoveFile(Bin, "/game/assets/buffers/crate.bin");
        fileSystem.MoveFile(Bin + ".meta", "/game/assets/buffers/crate.bin.meta");

        // Nothing has caught the sidecar up yet: the uri names nothing, and the guid finds the file.
        await Assert.That(ModelSource.ReadGlb(fileSystem, Gltf, index: Index(fileSystem))).IsNotEmpty();
        await Assert.That(Build(fileSystem).Errors).IsEmpty();

        // verify --fix catches the recorded path up; the .gltf is not touched, and a read with no
        // tree at hand finds the buffer where the sidecar now says it is.
        var repaired = ReferenceRepair.Fix(fileSystem, s_layout);

        await Assert.That(repaired.Single().Repointed).Contains(line => line.Contains("buffers[0]: models/crate.bin -> buffers/crate.bin"));
        await Assert.That(fileSystem.ReadAllBytes(Gltf)).IsEquivalentTo(json, CollectionOrdering.Matching);
        await Assert.That(ModelSource.ReadGlb(fileSystem, Gltf)).IsNotEmpty();
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task a_watcher_drain_after_a_bin_rename_records_it_and_leaves_the_gltf_alone()
    {
        using var fileSystem = ExtractedProject();
        var json = fileSystem.ReadAllBytes(Gltf);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var watcher = new AssetWatcher(fileSystem, s_layout, new SidecarMaintainer(fileSystem, s_layout), now: () => now);

        // A rename made outside the tool: the watcher carries the sidecar after it.
        fileSystem.CreateDirectory("/game/assets/buffers");
        fileSystem.MoveFile(Bin, "/game/assets/buffers/crate.bin");
        watcher.ObserveRename(Bin, "/game/assets/buffers/crate.bin");
        now += AssetWatcher.Debounce;
        watcher.Drain();

        await Assert.That(fileSystem.ReadAllBytes(Gltf)).IsEquivalentTo(json, CollectionOrdering.Matching);
        await Assert.That(MeshReferences.Recorded(fileSystem, Gltf).Single(entry => entry.Slot == "buffers[0]").Reference.Path).IsEqualTo("buffers/crate.bin");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
        await Assert.That(Build(fileSystem).Errors).IsEmpty();
    }

    [Test]
    public async Task a_bin_whose_recorded_guid_is_gone_and_whose_uri_names_nothing_is_refused_naming_the_slot()
    {
        using var fileSystem = ExtractedProject();
        fileSystem.DeleteFile(Bin);
        fileSystem.DeleteFile(Bin + ".meta");

        await Assert.That(() => ModelSource.ReadGlb(fileSystem, Gltf, index: Index(fileSystem))).Throws<InvalidDataException>()
            .WithMessageContaining("buffers[0] names 'crate.bin', which does not exist, and no asset carries guid");
    }

    [Test]
    public async Task removing_the_bin_names_the_gltf_that_reads_it()
    {
        using var fileSystem = ExtractedProject();

        var result = AssetRemover.Remove(fileSystem, s_layout, Bin);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Dangling.Single().ReferrerPath).IsEqualTo(new UPath(Gltf));
        await Assert.That(fileSystem.FileExists(Bin)).IsTrue();
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

    [Test]
    public async Task a_buffer_view_past_the_end_of_its_buffer_is_refused()
    {
        using var fileSystem = Project();
        WriteGltf(fileSystem, Crate(b => b.AddExternalImage("crate.png")));
        var gltf = Json(fileSystem, Gltf);
        // Concatenated into one BIN, the overrun would read whatever follows instead of failing.
        gltf["bufferViews"]![0]!["byteLength"] = gltf["buffers"]![0]!["byteLength"]!.GetValue<int>() + 1;
        fileSystem.WriteAllText(Gltf, gltf.ToJsonString());

        await Assert.That(() => ModelSource.ReadGlb(fileSystem, Gltf)).Throws<InvalidDataException>().WithMessageContaining("buffer view #0 spans bytes");
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

    /// <summary>A crate <c>.gltf</c> beside its <c>.bin</c> and <c>.png</c>, extracted, so its sidecar records both by identity.</summary>
    private static MemoryFileSystem ExtractedProject()
    {
        var fileSystem = Project();
        Texture(fileSystem, "/game/assets/models/crate.png");
        WriteGltf(fileSystem, Crate(b => b.AddExternalImage("crate.png")));
        var result = AssetExtractor.Extract(fileSystem, s_layout, Gltf);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join('\n', result.Errors));
        return fileSystem;
    }

    private static JsonObject Json(MemoryFileSystem fileSystem, UPath gltf) => JsonNode.Parse(fileSystem.ReadAllText(gltf))!.AsObject();

    private static AssetIndex Index(MemoryFileSystem fileSystem) => AssetIndex.Scan(fileSystem, s_layout.Assets);

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
