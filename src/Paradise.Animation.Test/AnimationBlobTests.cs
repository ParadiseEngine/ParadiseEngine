using System.Numerics;

namespace Paradise.Animation.Test;

/// <summary>Skeleton and clip blobs round-trip every field, refuse a foreign or inconsistent blob by name, and give the same bytes for the same data.</summary>
public class AnimationBlobTests
{
    private static SkeletonData Rig() => new(
        [
            new SkeletonNodeData("hip", -1, new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One),
            new SkeletonNodeData("knee", 0, Vector3.Zero, new Quaternion(0, 0, 0.7071068f, 0.7071068f), Vector3.One),
            new SkeletonNodeData(null, -1, Vector3.Zero, Quaternion.Identity, Vector3.One),
        ],
        [new SkinData("skin", [0, 1], [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)])]);

    private static ClipData Walk() => new("Walk",
        [
            new ClipChannelData(1, ChannelPath.Rotation, false, [0f, 0.5f], [0, 0, 0, 1, 0, 0, 0.7071068f, 0.7071068f]),
            new ClipChannelData(0, ChannelPath.Translation, true, [0f, 1f], [0, 1, 0, 0, 2, 0]),
        ]);

    [Test]
    public async Task a_skeleton_round_trips()
    {
        var read = SkeletonFormat.Read(SkeletonFormat.Write(Rig()));

        await Assert.That(read.Nodes).IsEquivalentTo(Rig().Nodes);
        await Assert.That(read.Skins.Count).IsEqualTo(1);
        await Assert.That(read.Skins[0].Name).IsEqualTo("skin");
        await Assert.That(read.Skins[0].JointNodes).IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(read.Skins[0].InverseBindMatrices[1]).IsEqualTo(Matrix4x4.CreateTranslation(0, -1, 0));
    }

    [Test]
    public async Task the_pinned_skeleton_finds_a_node_by_name()
    {
        using var reference = SkeletonFormat.Open(SkeletonFormat.Write(Rig()));

        await Assert.That(reference.Value.FindNode("KNEE")).IsEqualTo(1);
        await Assert.That(reference.Value.FindNode("toe")).IsEqualTo(-1);
        await Assert.That(reference.Value.Skins[0].JointCount).IsEqualTo(2);
    }

    [Test]
    public async Task a_clip_round_trips_with_its_duration()
    {
        var bytes = ClipFormat.Write(Walk());
        var read = ClipFormat.Read(bytes);

        await Assert.That(read.Name).IsEqualTo("Walk");
        await Assert.That(read.Channels).IsEquivalentTo(Walk().Channels);
        await Assert.That(read.Duration).IsEqualTo(1f);
        using var reference = ClipFormat.Open(bytes);
        await Assert.That(reference.Value.Duration).IsEqualTo(1f);
        await Assert.That(reference.Value.Channels[0].KeyCount).IsEqualTo(2);
    }

    [Test]
    public async Task the_same_data_gives_the_same_bytes()
    {
        await Assert.That(SkeletonFormat.Write(Rig())).IsEquivalentTo(SkeletonFormat.Write(Rig()));
        await Assert.That(ClipFormat.Write(Walk())).IsEquivalentTo(ClipFormat.Write(Walk()));
    }

    [Test]
    public async Task a_skin_naming_a_node_outside_the_tree_is_refused()
    {
        var broken = new SkeletonData(Rig().Nodes, [new SkinData("skin", [0, 9], [Matrix4x4.Identity, Matrix4x4.Identity])]);

        var error = await Assert.That(() => SkeletonFormat.Read(SkeletonFormat.Write(broken))).Throws<InvalidDataException>();

        await Assert.That(error!.Message).Contains("outside the tree");
    }

    [Test]
    public async Task a_channel_whose_values_do_not_match_its_keys_is_refused_at_write()
    {
        var broken = new ClipData("x", [new ClipChannelData(0, ChannelPath.Scale, false, [0f, 1f], [1, 1, 1])]);

        await Assert.That(() => ClipFormat.Write(broken)).Throws<ArgumentException>();
    }

    [Test]
    public async Task each_blob_refuses_the_other_by_magic()
    {
        var skeleton = SkeletonFormat.Write(Rig());
        var clip = ClipFormat.Write(Walk());

        await Assert.That(SkeletonFormat.IsSkeleton(skeleton)).IsTrue();
        await Assert.That(SkeletonFormat.IsSkeleton(clip)).IsFalse();
        await Assert.That(ClipFormat.IsClip(clip)).IsTrue();
        await Assert.That(() => ClipFormat.Read(skeleton)).Throws<InvalidDataException>();
        await Assert.That(() => SkeletonFormat.Read(clip)).Throws<InvalidDataException>();
    }
}
