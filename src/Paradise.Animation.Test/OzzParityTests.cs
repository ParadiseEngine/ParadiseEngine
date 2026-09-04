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
        var ours = SkeletonBuilder.Build(raw);
        var theirs = Skeleton.Load(TestRigs.Fixture("ozz-skeleton.ozz"));

        await Assert.That(ours.Names.ToArray()).IsEquivalentTo(theirs.Names.ToArray());
        await Assert.That(ours.Parents.ToArray()).IsEquivalentTo(theirs.Parents.ToArray());
        for (var i = 0; i < ours.JointCount; i++)
        {
            await Assert.That(ours.RestPoses[i].Translation).IsEqualTo(theirs.RestPoses[i].Translation);
            await Assert.That(Vector3.Distance(ours.RestPoses[i].Scale, theirs.RestPoses[i].Scale)).IsLessThanOrEqualTo(2e-7f);
            var rotation = ours.RestPoses[i].Rotation - theirs.RestPoses[i].Rotation;
            await Assert.That(MathF.Max(MathF.Max(MathF.Abs(rotation.X), MathF.Abs(rotation.Y)), MathF.Max(MathF.Abs(rotation.Z), MathF.Abs(rotation.W)))).IsLessThanOrEqualTo(2e-7f);
        }

        // Loading ozz's bytes and saving them again is the identity.
        await Assert.That(theirs.Save()).IsEquivalentTo(TestRigs.Fixture("ozz-skeleton.ozz"));
    }

    [Test]
    public async Task the_animation_builder_writes_the_bytes_ozz_writes()
    {
        var (raw, clip) = TestRigs.Parity();
        var skeleton = Skeleton.Load(TestRigs.Fixture("ozz-skeleton.ozz"));

        var ours = AnimationBuilder.Build(clip(skeleton), iframeInterval: 0.5f).Save();

        await Assert.That(ours).IsEquivalentTo(TestRigs.Fixture("ozz-animation.ozz"));
        await Assert.That(AnimationClip.Load(ours).Save()).IsEquivalentTo(ours);
    }

    [Test]
    public async Task the_optimizer_keeps_the_keys_ozz_keeps()
    {
        var (raw, clip) = TestRigs.Parity();
        var skeleton = Skeleton.Load(TestRigs.Fixture("ozz-skeleton.ozz"));

        var optimized = AnimationOptimizer.Optimize(clip(skeleton), skeleton, AnimationOptimizer.Setting.Default);
        var ours = AnimationBuilder.Build(optimized, iframeInterval: 0.5f).Save();

        await Assert.That(ours).IsEquivalentTo(TestRigs.Fixture("ozz-animation-optimized.ozz"));
    }

    [Test]
    public async Task an_archive_ozz_wrote_samples_here()
    {
        var skeleton = Skeleton.Load(TestRigs.Fixture("ozz-skeleton.ozz"));
        var animation = AnimationClip.Load(TestRigs.Fixture("ozz-animation.ozz"));
        var context = new SamplingContext(animation.TrackCount);
        var poses = new JointPose[animation.TrackCount];

        await Assert.That(animation.Name).IsEqualTo("parity");
        await Assert.That(animation.Duration).IsEqualTo(2.5f);
        await Assert.That(animation.TrackCount).IsEqualTo(skeleton.JointCount);
        await Assert.That(animation.Rotations.IframeDesc.Length).IsEqualTo(2 * 5);
        foreach (var ratio in new[] { 0f, 0.3f, 0.9f, 0.1f, 1f })
        {
            context.Sample(animation, ratio, poses);
            foreach (var pose in poses) await Assert.That(float.IsFinite(pose.Rotation.Length())).IsTrue();
        }
    }
}
