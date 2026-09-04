using System.Numerics;

using Paradise.Animation.Offline;
using Paradise.BLOB;

namespace Paradise.Animation.Test;

/// <summary>The sampler interpolates the way the builder laid keys out, seeks in both directions to the same pose, and pads unanimated joints with rest.</summary>
public class SamplingTests
{
    private const float Quantization = 2e-3f;

    private static (NativeBlobAssetReference<SkeletonBlob> Skeleton, NativeBlobAssetReference<AnimationBlob> Clip) Bend(float iframeInterval = 0f)
    {
        var skeleton = TestRigs.Chain();
        var clip = new ClipData("bend",
        [
            new ClipChannelData(1, ChannelPath.Rotation, false, [0f, 1f], [0, 0, 0, 1, TestRigs.QuarterTurnZ.X, TestRigs.QuarterTurnZ.Y, TestRigs.QuarterTurnZ.Z, TestRigs.QuarterTurnZ.W]),
            new ClipChannelData(0, ChannelPath.Translation, false, [0f, 0.5f, 1f], [0, 1, 0, 0, 2, 0, 0, 3, 0]),
            new ClipChannelData(2, ChannelPath.Scale, true, [0f, 0.5f], [1, 1, 1, 2, 2, 2]),
        ]);
        return (skeleton, AnimationBuilder.Build(ClipConverter.ToRaw(clip, ref skeleton.Value), iframeInterval));
    }

    [Test]
    public async Task keys_interpolate_linearly_and_unanimated_components_hold_the_rest_pose()
    {
        var (skeleton, clip) = Bend();
        using var _ = skeleton;
        using var __ = clip;
        using var context = SamplingContext.Create(skeleton.Value.JointCount);
        using var poses = JointPoses.Create(skeleton.Value.JointCount);

        context.Value.Sample(ref clip.Value, 0.25f, ref poses.Value);

        await Assert.That(Vector3.Distance(poses.Value[0].Translation, new Vector3(0, 1.5f, 0))).IsLessThan(Quantization);
        await Assert.That(Quaternion.Dot(poses.Value[0].Rotation, Quaternion.Identity)).IsGreaterThan(1f - Quantization);
        var expected = Quaternion.Normalize(Quaternion.Lerp(Quaternion.Identity, TestRigs.QuarterTurnZ, 0.25f));
        await Assert.That(Quaternion.Dot(poses.Value[1].Rotation, expected)).IsGreaterThan(1f - Quantization);
        await Assert.That(poses.Value[1].Translation).IsEqualTo(Vector3.Zero);
        await Assert.That(poses.Value[2].Scale).IsEqualTo(Vector3.One);
        await Assert.That(poses.Value[2].Translation).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task a_step_channel_holds_its_value_until_the_next_key()
    {
        var (skeleton, clip) = Bend();
        using var _ = skeleton;
        using var __ = clip;
        using var context = SamplingContext.Create(skeleton.Value.JointCount);
        using var poses = JointPoses.Create(skeleton.Value.JointCount);

        context.Value.Sample(ref clip.Value, 0.49f, ref poses.Value);
        var before = poses.Value[2].Scale;
        context.Value.Sample(ref clip.Value, 0.5f, ref poses.Value);
        var at = poses.Value[2].Scale;

        await Assert.That(before).IsEqualTo(Vector3.One);
        await Assert.That(Vector3.Distance(at, new Vector3(2, 2, 2))).IsLessThan(Quantization);
    }

    [Test]
    public async Task seeking_backwards_gives_the_pose_a_fresh_context_gives()
    {
        foreach (var interval in new[] { 0f, 0.25f })
        {
            var (skeleton, clip) = Bend(interval);
            using var _ = skeleton;
            using var __ = clip;
            using var walked = SamplingContext.Create(skeleton.Value.JointCount);
            using var poses = JointPoses.Create(skeleton.Value.JointCount);
            foreach (var ratio in new[] { 0f, 0.3f, 0.6f, 0.9f, 0.4f, 0.1f, 0.95f, 0.05f })
            {
                walked.Value.Sample(ref clip.Value, ratio, ref poses.Value);
                using var fresh = JointPoses.Create(skeleton.Value.JointCount);
                using var context = SamplingContext.Create(skeleton.Value.JointCount);
                context.Value.Sample(ref clip.Value, ratio, ref fresh.Value);
                await Assert.That(poses.Value.ToArray()).IsEquivalentTo(fresh.Value.ToArray());
            }
        }
    }

    [Test]
    public async Task the_ratio_is_clamped_and_a_context_too_small_is_refused()
    {
        var (skeleton, clip) = Bend();
        using var _ = skeleton;
        using var __ = clip;
        using var context = SamplingContext.Create(skeleton.Value.JointCount);
        using var small = SamplingContext.Create(0);
        using var poses = JointPoses.Create(skeleton.Value.JointCount);
        using var end = JointPoses.Create(skeleton.Value.JointCount);

        context.Value.Sample(ref clip.Value, 1f, ref end.Value);
        context.Value.Sample(ref clip.Value, 7f, ref poses.Value);

        await Assert.That(poses.Value.ToArray()).IsEquivalentTo(end.Value.ToArray());
        var error = await Assert.That(() => small.Value.Sample(ref clip.Value, 0f, ref poses.Value)).Throws<ArgumentException>();
        await Assert.That(error!.Message).Contains("at most 0");
    }

    [Test]
    public async Task a_context_reused_for_another_clip_restarts_its_cursor()
    {
        var (skeleton, first) = Bend();
        var (other, second) = Bend(0.25f);
        using var _ = skeleton;
        using var __ = first;
        using var ___ = other;
        using var ____ = second;
        using var context = SamplingContext.Create(skeleton.Value.JointCount);
        using var poses = JointPoses.Create(skeleton.Value.JointCount);
        using var expected = JointPoses.Create(skeleton.Value.JointCount);

        context.Value.Sample(ref first.Value, 0.9f, ref poses.Value);
        context.Value.Sample(ref second.Value, 0.3f, ref poses.Value);
        using var fresh = SamplingContext.Create(skeleton.Value.JointCount);
        fresh.Value.Sample(ref second.Value, 0.3f, ref expected.Value);

        await Assert.That(poses.Value.ToArray()).IsEquivalentTo(expected.Value.ToArray());
    }

    [Test]
    public async Task local_to_model_walks_the_parents_in_the_row_vector_convention()
    {
        using var skeleton = TestRigs.Chain();
        using var locals = JointPoses.Create(skeleton.Value.JointCount);
        locals.Value.CopyFrom(skeleton.Value.RestPoses.ToSpan());
        var models = new Matrix4x4[skeleton.Value.JointCount];

        LocalToModel.Compute(ref skeleton.Value, ref locals.Value, models, Matrix4x4.CreateTranslation(10, 0, 0));

        await Assert.That(models[0].Translation).IsEqualTo(new Vector3(10, 1, 0));
        await Assert.That(TestRigs.MaxAbs(models[1] - Matrix4x4.CreateFromQuaternion(TestRigs.QuarterTurnZ) * Matrix4x4.CreateTranslation(10, 1, 0))).IsLessThan(1e-6f);
        await Assert.That(models[2].Translation).IsEqualTo(new Vector3(10, 0, 0));
    }
}
