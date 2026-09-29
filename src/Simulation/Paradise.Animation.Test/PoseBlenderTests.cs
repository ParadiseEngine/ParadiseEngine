using System.Numerics;

using Paradise.Animation.Offline;
using Paradise.BLOB;

namespace Paradise.Animation.Test;

/// <summary>Weights are relative and every joint normalizes by the weight it received; rotations accumulate on one hemisphere and normalize once; joints nothing reached take the fallback.</summary>
public class PoseBlenderTests
{
    private static NativeBlobAssetReference<JointPoses> Poses(params JointPose[] poses)
    {
        var blob = JointPoses.Create(poses.Length);
        blob.Value.CopyFrom(poses);
        return blob;
    }

    private static JointPose At(float x) => new(new Vector3(x, 0f, 0f), Quaternion.Identity, Vector3.One);

    private static JointPose Turned(Quaternion rotation) => new(Vector3.Zero, rotation, Vector3.One);

    /// <summary>Five unparented joints: one more than a lane group, so the padding lanes are exercised.</summary>
    private static NativeBlobAssetReference<SkeletonBlob> Five()
    {
        var raw = new RawSkeleton();
        foreach (var name in new[] { "a", "b", "c", "d", "e" }) raw.Roots.Add(new RawJoint(name));
        return SkeletonBuilder.Build(raw);
    }

    private static float MaxDifference(float[] actual, float[] expected)
    {
        if (actual.Length != expected.Length) return float.PositiveInfinity;
        var max = 0f;
        for (var i = 0; i < actual.Length; i++) max = MathF.Max(max, MathF.Abs(actual[i] - expected[i]));
        return max;
    }

    [Test]
    public async Task weights_are_relative_and_translations_average_by_them()
    {
        using var zero = Poses(At(0f));
        using var ten = Poses(At(10f));
        using var twenty = Poses(At(20f));
        using var fallback = Poses(JointPose.Identity);
        using var output = JointPoses.Create(1);
        using var blender = PoseBlender.Create(1);
        var results = new List<float>();
        foreach (var scale in new[] { 1f, 7f, 1e-30f, 1e30f })
        {
            // Lightest first rescales the running sums on every add; heaviest first never does.
            blender.Value.Reset();
            blender.Value.Add(ref zero.Value, 0.2f * scale);
            blender.Value.Add(ref ten.Value, 0.3f * scale);
            blender.Value.Add(ref twenty.Value, 0.5f * scale);
            blender.Value.Resolve(ref fallback.Value, ref output.Value);
            results.Add(output.Value[0].Translation.X);

            blender.Value.Reset();
            blender.Value.Add(ref twenty.Value, 0.5f * scale);
            blender.Value.Add(ref ten.Value, 0.3f * scale);
            blender.Value.Add(ref zero.Value, 0.2f * scale);
            blender.Value.Resolve(ref fallback.Value, ref output.Value);
            results.Add(output.Value[0].Translation.X);
        }

        await Assert.That(blender.Value.Count).IsEqualTo(3);
        await Assert.That(MaxDifference([.. results], [.. Enumerable.Repeat(13f, results.Count)])).IsLessThan(1e-4f);
    }

    [Test]
    public async Task rotations_accumulate_once_and_normalize_once_whatever_the_input_order()
    {
        var turns = new[]
        {
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
        };
        // Every pair of these lies on one hemisphere, so the weighted mean is their normalized sum in any order.
        var mean = Quaternion.Normalize(turns[0] + turns[1] + turns[2]);
        using var fallback = Poses(JointPose.Identity);
        using var output = JointPoses.Create(1);
        using var blender = PoseBlender.Create(1);
        var errors = new List<float>();
        foreach (var order in new[] { new[] { 0, 1, 2 }, [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0] })
        {
            blender.Value.Reset();
            foreach (var index in order)
            {
                using var input = Poses(Turned(turns[index]));
                blender.Value.Add(ref input.Value, 1f);
            }

            blender.Value.Resolve(ref fallback.Value, ref output.Value);
            errors.Add(TestRigs.Angle(output.Value[0].Rotation, mean));
        }

        await Assert.That(errors.Max()).IsLessThan(1e-3f);
    }

    [Test]
    public async Task a_rotation_on_the_far_hemisphere_joins_the_near_one()
    {
        // −q is the same rotation as q; summed naively the two would cancel.
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        using var identity = Poses(JointPose.Identity);
        using var flipped = Poses(Turned(-quarter));
        using var output = JointPoses.Create(1);
        using var blender = PoseBlender.Create(1);
        blender.Value.Add(ref identity.Value, 1f);
        blender.Value.Add(ref flipped.Value, 1f);
        blender.Value.Resolve(ref identity.Value, ref output.Value);
        var halfway = output.Value[0].Rotation;

        blender.Value.Reset();
        blender.Value.Add(ref flipped.Value, 2f);
        blender.Value.Add(ref flipped.Value, 3f);
        blender.Value.Resolve(ref identity.Value, ref output.Value);

        await Assert.That(TestRigs.Angle(halfway, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f))).IsLessThan(1e-3f);
        await Assert.That(TestRigs.Angle(output.Value[0].Rotation, quarter)).IsLessThan(1e-3f);
    }

    [Test]
    public async Task nothing_added_resolves_to_the_fallback()
    {
        using var fallback = Poses(new JointPose(new Vector3(1f, 2f, 3f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(2f)));
        using var ignored = Poses(At(9f));
        using var output = JointPoses.Create(1);
        using var blender = PoseBlender.Create(1);
        blender.Value.Resolve(ref fallback.Value, ref output.Value);
        var empty = output.Value[0];
        blender.Value.Add(ref ignored.Value, 0f);
        blender.Value.Resolve(ref fallback.Value, ref output.Value);

        await Assert.That(empty).IsEqualTo(fallback.Value[0]);
        await Assert.That(blender.Value.Count).IsEqualTo(0);
        await Assert.That(output.Value[0]).IsEqualTo(fallback.Value[0]);
    }

    [Test]
    public async Task a_mask_limits_an_input_to_its_joints_and_a_trace_of_weight_eases_to_the_fallback()
    {
        using var skeleton = Five();
        using var ones = Poses(At(1f), At(1f), At(1f), At(1f), At(1f));
        using var threes = Poses(At(3f), At(3f), At(3f), At(3f), At(3f));
        using var fallback = Poses(At(0f), At(0f), At(0f), At(0f), At(0f));
        using var output = JointPoses.Create(5);
        using var blender = PoseBlender.Create(5);
        var partial = JointMask.Create(ref skeleton.Value, [0f, 1f, 0.5f, 0f, 0f]);
        var trace = JointMask.Create(ref skeleton.Value, [PoseBlender.MinimumJointWeight / 2f, 0f, 0f, 0f, 1f]);

        blender.Value.Add(ref ones.Value, 1f);
        blender.Value.Add(ref threes.Value, 1f, partial);
        blender.Value.Resolve(ref fallback.Value, ref output.Value);
        var mixed = output.Value.ToArray().Select(p => p.Translation.X).ToArray();

        blender.Value.Reset();
        blender.Value.Add(ref threes.Value, 1f, trace);
        blender.Value.Resolve(ref fallback.Value, ref output.Value);
        var alone = output.Value.ToArray().Select(p => p.Translation.X).ToArray();

        // Joint 2 weighs 1 against 0.5: (1 + 0.5 × 3) / 1.5.
        await Assert.That(MaxDifference(mixed, [1f, 2f, 2.5f / 1.5f, 1f, 1f])).IsLessThan(1e-5f);
        // Joint 0 reached half the minimum, so it sits halfway to the fallback; joints the mask excludes are the fallback exactly.
        await Assert.That(MaxDifference(alone, [1.5f, 0f, 0f, 0f, 3f])).IsLessThan(1e-4f);
    }

    [Test]
    public async Task invalid_weights_and_mismatched_sizes_are_refused()
    {
        using var skeleton = Five();
        using var one = Poses(At(1f));
        using var five = Poses(At(1f), At(1f), At(1f), At(1f), At(1f));
        using var blender = PoseBlender.Create(1);
        var mask = JointMask.Create(ref skeleton.Value, [1f, 1f, 1f, 1f, 1f]);

        await Assert.That(() => blender.Value.Add(ref one.Value, -1f)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => blender.Value.Add(ref one.Value, float.NaN)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => blender.Value.Add(ref one.Value, float.PositiveInfinity)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => blender.Value.Add(ref five.Value, 1f)).Throws<ArgumentException>();
        await Assert.That(() => blender.Value.Add(ref one.Value, 1f, mask)).Throws<ArgumentException>();
        await Assert.That(() => blender.Value.Resolve(ref one.Value, ref five.Value)).Throws<ArgumentException>();
        await Assert.That(blender.Value.Count).IsEqualTo(0);
    }
}
