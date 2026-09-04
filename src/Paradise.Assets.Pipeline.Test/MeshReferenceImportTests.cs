using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A <c>.mesh</c>, <c>.skeleton</c> or <c>.anim</c> document is cooked from the GLB it names at
/// build time: the blob lands at the document's path, a clip is found by name with the index as
/// the tiebreak, and what the GLB does not have is an error naming the document.
/// </summary>
public class MeshReferenceImportTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private const string Glb = "/game/assets/models/crate.glb";

    private static byte[] CrateGlb(float x = 0f, params string[] clips)
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([x, 0f, 0f, x + 1f, 0f, 0f, x, 1f, 0f], "VEC3");
        var node = b.AddNode(mesh: b.AddMesh(GlbTestBuilder.Primitive(position)), name: "Crate");
        var times = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var values = b.AddFloatAccessor([0f, 0f, 0f, 0f, 2f, 0f], "VEC3");
        foreach (var clip in clips) b.AddAnimation(clip, (node, "translation", times, values, null));
        b.SetSceneRoots(node);
        return b.Build();
    }

    private static (MemoryFileSystem FileSystem, AssetReference Source) Project(byte[]? glb = null)
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes(Glb, glb ?? CrateGlb(clips: "Bob"));
        var guid = ProjectVerifierTests.Mint(fileSystem, Glb);
        return (fileSystem, new AssetReference(guid, "models/crate.glb"));
    }

    private static string ClipName(byte[] archive)
    {
        using var clip = Paradise.Animation.OzzArchive.ReadAnimation(archive);
        return clip.Value.Name.ToString();
    }

    private static void Reference(MemoryFileSystem fileSystem, UPath path, MeshReferenceDocument document)
    {
        fileSystem.WriteAllBytes(path, document.WriteBytes());
        ProjectVerifierTests.Mint(fileSystem, path);
    }

    [Test]
    public async Task a_mesh_document_cooks_to_the_blob_at_its_own_path()
    {
        var (fileSystem, source) = Project(CrateGlb(x: 5f));
        using var _ = fileSystem;
        Reference(fileSystem, "/game/assets/models/crate.mesh", new MeshReferenceDocument(source, MeshSlot.Mesh));

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Errors).IsEmpty();
        var blob = Paradise.Assets.Mesh.MeshBlobFormat.Read(fileSystem.ReadAllBytes("/game/build/models/crate.mesh"));
        await Assert.That(blob.Vertices[0]).IsEqualTo(5f);
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsFalse();
    }

    [Test]
    public async Task a_clip_is_found_by_name_then_by_hash_then_by_index()
    {
        var glb = CrateGlb(0f, "Idle", "Run");
        var (fileSystem, source) = Project(glb);
        using var _ = fileSystem;
        var cooked = GltfCook.Cook(Paradise.Assets.Gltf.GltfSceneReader.ReadGeometry(glb));
        Reference(fileSystem, "/game/assets/models/crate.Run.anim", new MeshReferenceDocument(source, MeshSlot.Clip, "Run", 1));
        // Renamed in the DCC since the document was written, and the index points elsewhere: the hash finds it.
        Reference(fileSystem, "/game/assets/models/crate.Walk.anim", new MeshReferenceDocument(source, MeshSlot.Clip, "Walk", 1, GltfCook.ClipFingerprint(cooked.Clips[0])));
        // Renamed and re-exported: only the index is left.
        Reference(fileSystem, "/game/assets/models/crate.Jog.anim", new MeshReferenceDocument(source, MeshSlot.Clip, "Jog", 0, "0000"));

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(ClipName(fileSystem.ReadAllBytes("/game/build/models/crate.Run.anim"))).IsEqualTo("Run");
        await Assert.That(ClipName(fileSystem.ReadAllBytes("/game/build/models/crate.Walk.anim"))).IsEqualTo("Idle");
        await Assert.That(ClipName(fileSystem.ReadAllBytes("/game/build/models/crate.Jog.anim"))).IsEqualTo("Idle");
    }

    [Test]
    public async Task a_skeleton_document_cooks_to_an_ozz_archive_of_the_node_tree()
    {
        var (fileSystem, source) = Project();
        using var _ = fileSystem;
        Reference(fileSystem, "/game/assets/models/crate.skeleton", new MeshReferenceDocument(source, MeshSlot.Skeleton));

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Errors).IsEmpty();
        using var skeleton = Paradise.Animation.OzzArchive.ReadSkeleton(fileSystem.ReadAllBytes("/game/build/models/crate.skeleton"));
        await Assert.That(skeleton.Value.FindJoint("Crate")).IsEqualTo(0);
    }

    [Test]
    public async Task the_glb_sidecar_decides_whether_the_clips_are_decimated()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var node = b.AddNode(mesh: b.AddMesh(GlbTestBuilder.Primitive(position)), name: "Crate");
        var times = b.AddFloatAccessor([0f, 0.5f, 1f], "SCALAR");
        var values = b.AddFloatAccessor([0f, 0f, 0f, 0f, 1f, 0f, 0f, 2f, 0f], "VEC3");   // linear: the middle key is redundant
        b.AddAnimation("Rise", (node, "translation", times, values, null));
        b.SetSceneRoots(node);
        var (fileSystem, source) = Project(b.Build());
        using var _ = fileSystem;
        Reference(fileSystem, "/game/assets/models/crate.Rise.anim", new MeshReferenceDocument(source, MeshSlot.Clip, "Rise", 0));

        var lossless = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();
        await Assert.That(lossless.Errors).IsEmpty();
        using var every = Paradise.Animation.OzzArchive.ReadAnimation(fileSystem.ReadAllBytes("/game/build/models/crate.Rise.anim"));

        var meta = SidecarMeta.Load(fileSystem, Glb + ".meta");
        GlbImportSettings.WriteOptimization(meta, Paradise.Animation.Offline.AnimationOptimizer.Setting.Default);
        meta.Save(fileSystem, Glb + ".meta");
        var decimated = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();
        await Assert.That(decimated.Errors).IsEmpty();
        using var fewer = Paradise.Animation.OzzArchive.ReadAnimation(fileSystem.ReadAllBytes("/game/build/models/crate.Rise.anim"));

        await Assert.That(every.Value.Timepoints.Length).IsEqualTo(3);
        await Assert.That(fewer.Value.Timepoints.Length).IsEqualTo(2);
        await Assert.That(GlbImportSettings.ReadOptimization(SidecarMeta.Load(fileSystem, Glb + ".meta"))).IsEqualTo(Paradise.Animation.Offline.AnimationOptimizer.Setting.Default);
    }

    [Test]
    public async Task what_the_glb_does_not_have_is_an_error_naming_the_document()
    {
        var (fileSystem, source) = Project();
        using var _ = fileSystem;
        Reference(fileSystem, "/game/assets/models/crate.Jump.anim", new MeshReferenceDocument(source, MeshSlot.Clip, "Jump", 7));

        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Single()).Contains("crate.Jump.anim");
        await Assert.That(result.Errors.Single()).Contains("'Jump'");
        await Assert.That(result.Errors.Single()).Contains("'Bob'");
    }

    [Test]
    public async Task a_document_whose_glb_is_gone_is_an_error_and_a_moved_glb_is_followed()
    {
        var (fileSystem, source) = Project();
        using var _ = fileSystem;
        Reference(fileSystem, "/game/assets/models/crate.mesh", new MeshReferenceDocument(source, MeshSlot.Mesh));

        var moved = AssetMover.Move(fileSystem, s_layout, Glb, "/game/assets/props/crate.glb");
        await Assert.That(moved.Errors).IsEmpty();
        await Assert.That(moved.Rewritten).Contains("models/crate.mesh");
        await Assert.That(MeshReferenceDocument.Load(fileSystem, "/game/assets/models/crate.mesh").Source.Path).IsEqualTo("props/crate.glb");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run().Errors).IsEmpty();

        var removed = AssetRemover.Remove(fileSystem, s_layout, "/game/assets/props/crate.glb", force: true);
        await Assert.That(removed.Dangling.Any(d => d.ReferrerPath.GetName() == "crate.mesh" && d.Where == "source")).IsTrue();
        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();
        await Assert.That(result.Errors.Single()).Contains("no asset under assets/ carries");
    }

    [Test]
    public async Task verify_reports_a_document_for_a_clip_the_glb_no_longer_has()
    {
        var (fileSystem, source) = Project();
        using var _ = fileSystem;
        Reference(fileSystem, "/game/assets/models/crate.Bob.anim", new MeshReferenceDocument(source, MeshSlot.Clip, "Bob", 0));
        Reference(fileSystem, "/game/assets/models/crate.Jump.anim", new MeshReferenceDocument(source, MeshSlot.Clip, "Jump", 7, "0000"));

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        var stale = findings.Single(f => f.Severity == VerifySeverity.Error);
        await Assert.That(stale.Path.GetName()).IsEqualTo("crate.Jump.anim");
        await Assert.That(stale.Message).Contains("no longer has");
        await Assert.That(stale.Message).Contains("'Jump'");
    }

    [Test]
    public async Task verify_reports_a_document_whose_slot_disagrees_with_its_extension()
    {
        var (fileSystem, source) = Project();
        using var _ = fileSystem;
        Reference(fileSystem, "/game/assets/models/crate.mesh", new MeshReferenceDocument(source, MeshSlot.Skeleton));

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Single(f => f.Severity == VerifySeverity.Error).Message).Contains("extension says 'mesh'");
    }
}
