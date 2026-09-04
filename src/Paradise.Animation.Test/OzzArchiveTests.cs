using System.Numerics;

using Paradise.Animation.Offline;

namespace Paradise.Animation.Test;

/// <summary>Archives round-trip every field, and a foreign, big-endian or newer archive is refused by name.</summary>
public class OzzArchiveTests
{
    [Test]
    public async Task a_skeleton_round_trips_in_depth_first_order()
    {
        var skeleton = TestRigs.Chain();

        var read = Skeleton.Load(skeleton.Save());

        await Assert.That(read.Names.ToArray()).IsEquivalentTo(new[] { "hip", "knee", "prop" });
        await Assert.That(read.Parents.ToArray()).IsEquivalentTo(new short[] { -1, 0, -1 });
        await Assert.That(read.RestPoses[0].Translation).IsEqualTo(new Vector3(0, 1, 0));
        await Assert.That(read.RestPoses[1].Rotation).IsEqualTo(TestRigs.QuarterTurnZ);
        await Assert.That(read.FindJoint("knee")).IsEqualTo(1);
        await Assert.That(read.FindJoint("toe")).IsEqualTo(-1);
        await Assert.That(read.IsLeaf(0)).IsFalse();
        await Assert.That(read.IsLeaf(1)).IsTrue();
        await Assert.That(Skeleton.IsSkeleton(skeleton.Save())).IsTrue();
        await Assert.That(AnimationClip.IsAnimation(skeleton.Save())).IsFalse();
    }

    [Test]
    public async Task an_empty_skeleton_is_a_valid_archive()
    {
        var empty = SkeletonBuilder.Build(new RawSkeleton());

        await Assert.That(Skeleton.Load(empty.Save()).JointCount).IsEqualTo(0);
    }

    [Test]
    public async Task a_clip_round_trips_with_its_name_duration_and_streams()
    {
        var skeleton = TestRigs.Chain();
        var raw = new RawAnimation { Name = "Walk", Duration = 2f };
        for (var i = 0; i < skeleton.JointCount; i++) raw.Tracks.Add(new RawTrack());
        raw.Tracks[1].Rotations.Add(new RotationKey(0f, Quaternion.Identity));
        raw.Tracks[1].Rotations.Add(new RotationKey(2f, TestRigs.QuarterTurnZ));
        var built = AnimationBuilder.Build(raw, iframeInterval: 1f);

        var bytes = built.Save();
        var read = AnimationClip.Load(bytes);

        await Assert.That(read.Name).IsEqualTo("Walk");
        await Assert.That(read.Duration).IsEqualTo(2f);
        await Assert.That(read.TrackCount).IsEqualTo(3);
        await Assert.That(read.Timepoints).IsEquivalentTo(new[] { 0f, 1f });
        await Assert.That(read.Rotations.KeyCount).IsEqualTo(AnimationClip.PaddedTrackCount(3) * 2);
        await Assert.That(read.Save()).IsEquivalentTo(bytes);
        await Assert.That(AnimationClip.IsAnimation(bytes)).IsTrue();
    }

    [Test]
    public async Task foreign_big_endian_and_newer_archives_are_refused_by_name()
    {
        var skeleton = TestRigs.Chain().Save();
        var bigEndian = (byte[])skeleton.Clone();
        bigEndian[0] = 0;
        var newer = (byte[])skeleton.Clone();
        BitConverter.TryWriteBytes(newer.AsSpan(1 + Skeleton.Tag.Length + 1), Skeleton.Version + 1);

        var foreign = await Assert.That(() => Skeleton.Load("not an archive"u8.ToArray())).Throws<InvalidDataException>();
        await Assert.That(foreign!.Message).Contains("ozz-skeleton");
        var wrongKind = await Assert.That(() => AnimationClip.Load(skeleton)).Throws<InvalidDataException>();
        await Assert.That(wrongKind!.Message).Contains("ozz-animation");
        var endian = await Assert.That(() => Skeleton.Load(bigEndian)).Throws<InvalidDataException>();
        await Assert.That(endian!.Message).Contains("big-endian");
        var version = await Assert.That(() => Skeleton.Load(newer)).Throws<InvalidDataException>();
        await Assert.That(version!.Message).Contains("version 3");
        var truncated = await Assert.That(() => Skeleton.Load(skeleton.AsSpan(0, skeleton.Length - 8).ToArray())).Throws<InvalidDataException>();
        await Assert.That(truncated!.Message).Contains("ends inside");
    }

    [Test]
    public async Task a_skeleton_whose_parent_follows_its_child_is_refused()
    {
        var error = await Assert.That(() => new Skeleton(["a", "b"], [1, -1], [JointPose.Identity, JointPose.Identity])).Throws<ArgumentException>();

        await Assert.That(error!.Message).Contains("depth-first");
    }
}
