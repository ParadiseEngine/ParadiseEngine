using System.Numerics;

using Paradise.Animation.Offline;

namespace Paradise.Animation.Test;

/// <summary>
/// The format contract with ozz-animation 0.17, held as bytes: archives its own C++ builders wrote
/// from a procedural rig load here, and this assembly's builders write the same bytes from the
/// same raw input. The rest pose is compared within one float ulp rather than byte for byte
/// because ozz's arm64 build fuses multiply-adds (in the generator's <c>1 + x * 0.1</c> and in
/// its quaternion normalization); the clip is exact because quantization absorbs that.
/// </summary>
public class OzzParityTests
{
    [Test]
    public async Task the_skeleton_builder_matches_ozz_within_an_ulp()
    {
        var (raw, _) = TestRigs.Parity();
        using var ours = SkeletonBuilder.Build(raw);
        using var theirs = OzzArchive.ReadSkeleton(TestRigs.Fixture("ozz-skeleton.ozz"));

        await Assert.That(ours.Value.JointCount).IsEqualTo(theirs.Value.JointCount);
        await Assert.That(ours.Value.Parents.ToArray()).IsEquivalentTo(theirs.Value.Parents.ToArray());
        for (var i = 0; i < ours.Value.JointCount; i++)
        {
            var mine = ours.Value.RestPoses[i];
            var reference = theirs.Value.RestPoses[i];
            await Assert.That(ours.Value.Names[i].ToString()).IsEqualTo(theirs.Value.Names[i].ToString());
            await Assert.That(mine.Translation).IsEqualTo(reference.Translation);
            await Assert.That(Vector3.Distance(mine.Scale, reference.Scale)).IsLessThanOrEqualTo(2e-7f);
            var rotation = mine.Rotation - reference.Rotation;
            await Assert.That(MathF.Max(MathF.Max(MathF.Abs(rotation.X), MathF.Abs(rotation.Y)), MathF.Max(MathF.Abs(rotation.Z), MathF.Abs(rotation.W)))).IsLessThanOrEqualTo(2e-7f);
        }

        // Loading ozz's bytes and saving them again is the identity.
        await Assert.That(OzzArchive.WriteSkeleton(ref theirs.Value)).IsEquivalentTo(TestRigs.Fixture("ozz-skeleton.ozz"));
    }

    [Test]
    public async Task the_animation_builder_writes_the_bytes_ozz_writes()
    {
        var (_, clip) = TestRigs.Parity();
        using var built = AnimationBuilder.Build(clip(37), iframeInterval: 0.5f);

        var ours = OzzArchive.WriteAnimation(ref built.Value);

        await Assert.That(ours).IsEquivalentTo(TestRigs.Fixture("ozz-animation.ozz"));
        using var reread = OzzArchive.ReadAnimation(ours);
        await Assert.That(OzzArchive.WriteAnimation(ref reread.Value)).IsEquivalentTo(ours);
    }

    [Test]
    public async Task the_optimizer_keeps_the_keys_ozz_keeps()
    {
        var (_, clip) = TestRigs.Parity();
        using var skeleton = OzzArchive.ReadSkeleton(TestRigs.Fixture("ozz-skeleton.ozz"));

        var optimized = AnimationOptimizer.Optimize(clip(37), ref skeleton.Value, AnimationOptimizer.Setting.Default);
        using var built = AnimationBuilder.Build(optimized, iframeInterval: 0.5f);

        await Assert.That(OzzArchive.WriteAnimation(ref built.Value)).IsEquivalentTo(TestRigs.Fixture("ozz-animation-optimized.ozz"));
    }

    [Test]
    public async Task an_archive_ozz_wrote_samples_here()
    {
        using var skeleton = OzzArchive.ReadSkeleton(TestRigs.Fixture("ozz-skeleton.ozz"));
        using var clip = OzzArchive.ReadAnimation(TestRigs.Fixture("ozz-animation.ozz"));
        using var context = SamplingContext.Create(clip.Value.TrackCount);
        var poses = new JointPose[clip.Value.TrackCount];

        await Assert.That(clip.Value.Name.ToString()).IsEqualTo("parity");
        await Assert.That(clip.Value.Duration).IsEqualTo(2.5f);
        await Assert.That(clip.Value.TrackCount).IsEqualTo(skeleton.Value.JointCount);
        await Assert.That(clip.Value.Rotations.IframeDesc.Length).IsEqualTo(2 * 5);
        foreach (var ratio in new[] { 0f, 0.3f, 0.9f, 0.1f, 1f })
        {
            context.Value.Sample(ref clip.Value, ratio, poses);
            foreach (var pose in poses) await Assert.That(float.IsFinite(pose.Rotation.Length())).IsTrue();
        }
    }
}
