using System.Numerics;

using Paradise.Animation.Offline;

namespace Paradise.Animation.Test;

/// <summary>Archives round-trip every field into and out of the blobs, and a foreign, big-endian or newer archive is refused by name.</summary>
public class OzzArchiveTests
{
    [Test]
    public async Task a_skeleton_round_trips_in_depth_first_order()
    {
        using var skeleton = TestRigs.Chain();
        var archive = OzzArchive.WriteSkeleton(ref skeleton.Value);

        using var read = OzzArchive.ReadSkeleton(archive);

        await Assert.That(read.Value.JointCount).IsEqualTo(3);
        await Assert.That(read.Value.Names[0].ToString()).IsEqualTo("hip");
        await Assert.That(read.Value.Names[2].ToString()).IsEqualTo("prop");
        await Assert.That(read.Value.Parents.ToArray()).IsEquivalentTo(new short[] { -1, 0, -1 });
        await Assert.That(read.Value.RestPoses[0].Translation).IsEqualTo(new Vector3(0, 1, 0));
        await Assert.That(read.Value.RestPoses[1].Rotation).IsEqualTo(TestRigs.QuarterTurnZ);
        await Assert.That(read.Value.FindJoint("knee")).IsEqualTo(1);
        await Assert.That(read.Value.FindJoint("knee"u8)).IsEqualTo(1);
        await Assert.That(read.Value.FindJoint("toe")).IsEqualTo(-1);
        await Assert.That(read.Value.IsLeaf(0)).IsFalse();
        await Assert.That(read.Value.IsLeaf(1)).IsTrue();
        await Assert.That(OzzArchive.IsSkeleton(archive)).IsTrue();
        await Assert.That(OzzArchive.IsAnimation(archive)).IsFalse();
    }

    [Test]
    public async Task an_empty_skeleton_is_a_valid_archive()
    {
        using var empty = SkeletonBuilder.Build(new RawSkeleton());

        using var read = OzzArchive.ReadSkeleton(OzzArchive.WriteSkeleton(ref empty.Value));

        await Assert.That(read.Value.JointCount).IsEqualTo(0);
    }

    [Test]
    public async Task a_clip_round_trips_with_its_name_duration_and_streams()
    {
        using var skeleton = TestRigs.Chain();
        var raw = new RawAnimation { Name = "Walk", Duration = 2f };
        for (var i = 0; i < skeleton.Value.JointCount; i++) raw.Tracks.Add(new RawTrack());
        raw.Tracks[1].Rotations.Add(new RotationKey(0f, Quaternion.Identity));
        raw.Tracks[1].Rotations.Add(new RotationKey(2f, TestRigs.QuarterTurnZ));
        using var built = AnimationBuilder.Build(raw, iframeInterval: 1f);

        var bytes = OzzArchive.WriteAnimation(ref built.Value);
        using var read = OzzArchive.ReadAnimation(bytes);

        await Assert.That(read.Value.Name.ToString()).IsEqualTo("Walk");
        await Assert.That(read.Value.Duration).IsEqualTo(2f);
        await Assert.That(read.Value.TrackCount).IsEqualTo(3);
        await Assert.That(read.Value.Timepoints.ToArray()).IsEquivalentTo(new[] { 0f, 1f });
        await Assert.That(read.Value.Rotations.KeyCount).IsEqualTo(AnimationBlob.PaddedTrackCount(3) * 2);
        await Assert.That(OzzArchive.WriteAnimation(ref read.Value)).IsEquivalentTo(bytes);
        await Assert.That(OzzArchive.IsAnimation(bytes)).IsTrue();
    }

    [Test]
    public async Task foreign_big_endian_and_newer_archives_are_refused_by_name()
    {
        using var chain = TestRigs.Chain();
        var skeleton = OzzArchive.WriteSkeleton(ref chain.Value);
        var bigEndian = (byte[])skeleton.Clone();
        bigEndian[0] = 0;
        var newer = (byte[])skeleton.Clone();
        BitConverter.TryWriteBytes(newer.AsSpan(1 + OzzArchive.SkeletonTag.Length + 1), OzzArchive.SkeletonVersion + 1);

        var foreign = await Assert.That(() => OzzArchive.ReadSkeleton("not an archive"u8.ToArray())).Throws<InvalidDataException>();
        await Assert.That(foreign!.Message).Contains("ozz-skeleton");
        var wrongKind = await Assert.That(() => OzzArchive.ReadAnimation(skeleton)).Throws<InvalidDataException>();
        await Assert.That(wrongKind!.Message).Contains("ozz-animation");
        var endian = await Assert.That(() => OzzArchive.ReadSkeleton(bigEndian)).Throws<InvalidDataException>();
        await Assert.That(endian!.Message).Contains("big-endian");
        var version = await Assert.That(() => OzzArchive.ReadSkeleton(newer)).Throws<InvalidDataException>();
        await Assert.That(version!.Message).Contains("version 3");
        var truncated = await Assert.That(() => OzzArchive.ReadSkeleton(skeleton.AsSpan(0, skeleton.Length - 8).ToArray())).Throws<InvalidDataException>();
        await Assert.That(truncated!.Message).Contains("ends inside");
    }

    [Test]
    public async Task a_clip_whose_iframes_or_first_key_would_break_the_sampler_is_refused_on_read()
    {
        using var skeleton = TestRigs.Chain();
        var raw = new RawAnimation { Name = "Walk", Duration = 2f };
        for (var i = 0; i < skeleton.Value.JointCount; i++) raw.Tracks.Add(new RawTrack());
        raw.Tracks[1].Rotations.Add(new RotationKey(0f, Quaternion.Identity));
        raw.Tracks[1].Rotations.Add(new RotationKey(1f, TestRigs.QuarterTurnZ));
        raw.Tracks[1].Rotations.Add(new RotationKey(2f, Quaternion.Identity));
        using var built = AnimationBuilder.Build(raw, iframeInterval: 0.5f);
        var bytes = OzzArchive.WriteAnimation(ref built.Value);

        // Each stream's block is ratios, back-links, i-frame entries, i-frame table, interval, values; the
        // translation stream (rest keys only) has no i-frames, the rotation stream after it does.
        var header = 1 + OzzArchive.AnimationTag.Length + 1 + 4 + 13 * 4;
        var stream = header + built.Value.Name.Length + built.Value.Timepoints.Length * 4;
        var translationBlock = built.Value.Translations.Ratios.Length + built.Value.Translations.Previouses.Length * 2 + built.Value.Translations.IframeEntries.Length
            + built.Value.Translations.IframeDesc.Length * 4 + 4 + built.Value.Translations.Values.Length * 2;
        var rotationStream = stream + translationBlock;
        var iframeTable = rotationStream + built.Value.Rotations.Ratios.Length + built.Value.Rotations.Previouses.Length * 2 + built.Value.Rotations.IframeEntries.Length;
        var interval = iframeTable + built.Value.Rotations.IframeDesc.Length * 4;
        var timepointCount = built.Value.Timepoints.Length;
        await Assert.That(built.Value.Rotations.IframeDesc.Length).IsGreaterThan(0);

        var zeroInterval = (byte[])bytes.Clone();
        BitConverter.TryWriteBytes(zeroInterval.AsSpan(interval), 0f);
        var offsetPastEntries = (byte[])bytes.Clone();
        BitConverter.TryWriteBytes(offsetPastEntries.AsSpan(iframeTable), 1_000_000u);
        var lateFirstKey = (byte[])bytes.Clone();
        lateFirstKey[stream] = (byte)(timepointCount - 1);

        var a = await Assert.That(() => OzzArchive.ReadAnimation(zeroInterval)).Throws<InvalidDataException>();
        await Assert.That(a!.Message).Contains("interval of 0");
        var b = await Assert.That(() => OzzArchive.ReadAnimation(offsetPastEntries)).Throws<InvalidDataException>();
        await Assert.That(b!.Message).Contains("starts at byte 1000000");
        var c = await Assert.That(() => OzzArchive.ReadAnimation(lateFirstKey)).Throws<InvalidDataException>();
        await Assert.That(c!.Message).Contains("not at the clip's start");
    }

    [Test]
    public async Task a_skeleton_whose_parent_follows_its_child_is_refused()
    {
        var error = await Assert.That(() => SkeletonBlob.Create(["a", "b"], [1, -1], [JointPose.Identity, JointPose.Identity])).Throws<ArgumentException>();

        await Assert.That(error!.Message).Contains("depth-first");
    }

    [Test]
    public async Task a_clip_blob_round_trips_and_fingerprints_by_its_channels()
    {
        var clip = new ClipData("Walk",
        [
            new ClipChannelData(1, ChannelPath.Rotation, false, [0f, 0.5f], [0, 0, 0, 1, 0, 0, 0.7071068f, 0.7071068f]),
            new ClipChannelData(0, ChannelPath.Translation, true, [0f, 1f], [0, 1, 0, 0, 2, 0]),
        ]);

        var bytes = ClipFormat.Write(clip);
        var read = ClipFormat.Read(bytes);
        using var opened = ClipFormat.Open(bytes);

        await Assert.That(read.Name).IsEqualTo("Walk");
        await Assert.That(read.Channels).IsEquivalentTo(clip.Channels);
        await Assert.That(read.Duration).IsEqualTo(1f);
        await Assert.That(opened.Value.Duration).IsEqualTo(1f);
        await Assert.That(opened.Value.Channels[1].Step).IsTrue();
        await Assert.That(ClipFormat.Write(clip)).IsEquivalentTo(bytes);
        await Assert.That(ClipFormat.IsClip(bytes)).IsTrue();
        await Assert.That(() => ClipFormat.Write(new ClipData("x", [new ClipChannelData(0, ChannelPath.Scale, false, [0f, 1f], [1, 1, 1])]))).Throws<ArgumentException>();
        await Assert.That(() => ClipFormat.Read("nope"u8.ToArray())).Throws<InvalidDataException>();
    }
}
