using System.Numerics;

using Paradise.Animation.Offline;
using Paradise.BLOB;

namespace Paradise.Animation.Test;

/// <summary>A delta applied at full weight to its reference reproduces the source; a cooked clip resamples to the same deltas as its raw keys; a reference no delta can be taken against is refused.</summary>
public class AdditiveAnimationTests
{
    /// <summary>Two quantizations stack here, the source's and the delta's, so the bound is twice the sampler's.</summary>
    private const float Tolerance = 5e-3f;

    private static readonly float[] s_ratios = [0f, 0.1f, 0.25f, 0.5f, 0.77f, 1f];

    /// <summary>On the chain: the hip bobs, the knee turns from rest a further 60° about X, and the prop stretches.</summary>
    private static RawAnimation Wave(ref SkeletonBlob skeleton)
    {
        var from = TestRigs.QuarterTurnZ;
        var to = Quaternion.Normalize(from * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 3f));
        return ClipConverter.ToRaw(new ClipData("wave",
        [
            new ClipChannelData(0, ChannelPath.Translation, false, [0f, 0.5f, 1f], [0f, 1f, 0f, 0.5f, 1.25f, 0f, 0f, 1f, 0f]),
            new ClipChannelData(1, ChannelPath.Rotation, false, [0f, 1f], [from.X, from.Y, from.Z, from.W, to.X, to.Y, to.Z, to.W]),
            new ClipChannelData(2, ChannelPath.Scale, false, [0f, 1f], [1f, 1f, 1f, 2f, 1.5f, 1f]),
        ]), ref skeleton);
    }

    /// <summary>The largest translation or scale distance, or rotation angle, between two poses of the same joints.</summary>
    private static float Difference(JointPose[] a, JointPose[] b)
    {
        var worst = 0f;
        for (var joint = 0; joint < a.Length; joint++)
        {
            worst = MathF.Max(worst, Vector3.Distance(a[joint].Translation, b[joint].Translation));
            worst = MathF.Max(worst, TestRigs.Angle(a[joint].Rotation, b[joint].Rotation));
            worst = MathF.Max(worst, Vector3.Distance(a[joint].Scale, b[joint].Scale));
        }

        return worst;
    }

    [Test]
    public async Task a_delta_applied_to_its_reference_reproduces_the_source()
    {
        using var skeleton = TestRigs.Chain();
        var source = Wave(ref skeleton.Value);
        using var clip = AnimationBuilder.Build(source);
        using var delta = AdditiveAnimationBuilder.Build(source);
        using var context = SamplingContext.Create(3);
        using var deltaContext = SamplingContext.Create(3);
        using var reference = JointPoses.Create(3);
        using var expected = JointPoses.Create(3);
        using var sampled = JointPoses.Create(3);
        using var applied = JointPoses.Create(3);

        // ozz's default reference is the pose the clip starts in.
        context.Value.Sample(ref clip.Value, 0f, ref reference.Value);
        var worst = 0f;
        foreach (var ratio in s_ratios)
        {
            context.Value.Sample(ref clip.Value, ratio, ref expected.Value);
            deltaContext.Value.Sample(ref delta.Value.Deltas, ratio, ref sampled.Value);
            JointPoses.ApplyAdditive(ref reference.Value, ref sampled.Value, 1f, null, ref applied.Value);
            worst = MathF.Max(worst, Difference(applied.Value.ToArray(), expected.Value.ToArray()));
        }

        await Assert.That(worst).IsLessThan(Tolerance);
        await Assert.That(delta.Value.Deltas.Name.ToString()).IsEqualTo("wave");
        await Assert.That(delta.Value.Deltas.Duration).IsEqualTo(clip.Value.Duration);
    }

    [Test]
    public async Task a_cooked_clip_resamples_to_the_deltas_of_its_raw_keys()
    {
        using var skeleton = TestRigs.Chain();
        var source = Wave(ref skeleton.Value);
        var rest = skeleton.Value.RestPoses.ToArray();
        using var clip = AnimationBuilder.Build(source);
        using var fromKeys = AdditiveAnimationBuilder.Build(source, rest);
        using var fromClip = AdditiveAnimationBuilder.Build(ref clip.Value, rest);
        using var keysContext = SamplingContext.Create(3);
        using var clipContext = SamplingContext.Create(3);
        using var keysPose = JointPoses.Create(3);
        using var clipPose = JointPoses.Create(3);

        var worst = 0f;
        foreach (var ratio in s_ratios)
        {
            keysContext.Value.Sample(ref fromKeys.Value.Deltas, ratio, ref keysPose.Value);
            clipContext.Value.Sample(ref fromClip.Value.Deltas, ratio, ref clipPose.Value);
            worst = MathF.Max(worst, Difference(keysPose.Value.ToArray(), clipPose.Value.ToArray()));
        }

        await Assert.That(worst).IsLessThan(Tolerance);
        await Assert.That(fromClip.Value.Deltas.TrackCount).IsEqualTo(3);
        await Assert.That(fromClip.Value.Deltas.Duration).IsEqualTo(clip.Value.Duration);
    }

    [Test]
    public async Task a_reference_no_delta_can_be_taken_against_is_refused()
    {
        using var skeleton = TestRigs.Chain();
        var source = Wave(ref skeleton.Value);
        using var clip = AnimationBuilder.Build(source);
        var rest = skeleton.Value.RestPoses.ToArray();
        var flat = (JointPose[])rest.Clone();
        flat[1] = new JointPose(rest[1].Translation, rest[1].Rotation, new Vector3(1f, 0f, 1f));
        var aimless = (JointPose[])rest.Clone();
        aimless[2] = new JointPose(Vector3.Zero, default, Vector3.One);
        var squashed = new RawAnimation { Name = "squashed", Duration = 1f };
        squashed.Tracks.Add(new RawTrack());
        squashed.Tracks[0].Scales.Add(new ScaleKey(0f, new Vector3(0f, 1f, 1f)));

        var zeroScale = await Assert.That(() => AdditiveAnimationBuilder.Build(source, flat)).Throws<ArgumentException>();
        await Assert.That(zeroScale!.Message).Contains("Track 1");
        await Assert.That(() => AdditiveAnimationBuilder.Build(ref clip.Value, flat)).Throws<ArgumentException>();
        await Assert.That(() => AdditiveAnimationBuilder.Build(source, aimless)).Throws<ArgumentException>();
        await Assert.That(() => AdditiveAnimationBuilder.Build(source, rest.AsSpan(0, 2))).Throws<ArgumentException>();
        await Assert.That(() => AdditiveAnimationBuilder.Build(squashed)).Throws<ArgumentException>();
    }
}
