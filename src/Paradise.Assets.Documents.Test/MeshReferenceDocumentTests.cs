using Paradise.Authoring;

namespace Paradise.Assets.Documents.Test;

/// <summary>A mesh reference names one part of a GLB: read strictly, written canonically, and the extension is the slot.</summary>
public class MeshReferenceDocumentTests
{
    private static readonly AssetReference s_source = new(Guid.Parse("11111111-2222-4333-8444-555555555555"), "models/crate.glb");

    [Test]
    public async Task a_clip_round_trips_with_its_name_and_index()
    {
        var document = new MeshReferenceDocument(s_source, MeshSlot.Clip, "Bob", 2);

        var parsed = MeshReferenceDocument.Parse(document.Write(), "crate.Bob.anim");

        await Assert.That(parsed).IsEqualTo(document);
        await Assert.That(document.Write()).Contains("slot = \"clip\"");
    }

    [Test]
    public async Task a_mesh_carries_no_name_and_a_clip_needs_one_or_an_index()
    {
        var mesh = new MeshReferenceDocument(s_source, MeshSlot.Mesh);
        await Assert.That(MeshReferenceDocument.Parse(mesh.Write(), "crate.mesh")).IsEqualTo(mesh);

        var named = await Assert.That(() => MeshReferenceDocument.Parse(mesh.Write() + "name = \"Bob\"\n", "crate.mesh")).Throws<FormatException>();
        await Assert.That(named!.Message).Contains("carries no 'name'");

        var clip = new MeshReferenceDocument(s_source, MeshSlot.Clip).Write();
        var unnamed = await Assert.That(() => MeshReferenceDocument.Parse(clip, "crate.anim")).Throws<FormatException>();
        await Assert.That(unnamed!.Message).Contains("'name' or 'index'");
    }

    [Test]
    public async Task the_source_is_a_reference_and_the_version_is_pinned()
    {
        var noSource = await Assert.That(() => MeshReferenceDocument.Parse("schema_version = 1\nslot = \"mesh\"\n", "x.mesh")).Throws<FormatException>();
        await Assert.That(noSource!.Message).Contains("'source'");

        var newer = await Assert.That(() => MeshReferenceDocument.Parse("schema_version = 2\nslot = \"mesh\"\nsource = { guid = \"11111111-2222-4333-8444-555555555555\", path = \"a.glb\" }\n", "x.mesh")).Throws<FormatException>();
        await Assert.That(newer!.Message).Contains("schema_version = 2");

        var unknown = await Assert.That(() => MeshReferenceDocument.Parse("schema_version = 1\nslot = \"rig\"\nsource = { guid = \"11111111-2222-4333-8444-555555555555\", path = \"a.glb\" }\n", "x.mesh")).Throws<FormatException>();
        await Assert.That(unknown!.Message).Contains("'rig'");
    }

    [Test]
    public async Task the_extension_names_the_slot()
    {
        await Assert.That(MeshReferenceDocument.SlotOf("/a/crate.mesh")).IsEqualTo(MeshSlot.Mesh);
        await Assert.That(MeshReferenceDocument.SlotOf("/a/crate.SKELETON")).IsEqualTo(MeshSlot.Skeleton);
        await Assert.That(MeshReferenceDocument.SlotOf("/a/crate.Bob.anim")).IsEqualTo(MeshSlot.Clip);
        await Assert.That(MeshReferenceDocument.SlotOf("/a/crate.glb")).IsNull();
    }
}
