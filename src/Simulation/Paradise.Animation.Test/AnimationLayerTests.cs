using System.Numerics;

using Paradise.Animation.Offline;
using Paradise.BLOB;

namespace Paradise.Animation.Test;

/// <summary>Layers compose bottom to top: an override replaces the joints its mask admits by its weight, additive layers apply their deltas in order, and clips, masks and layers refuse what does not fit them.</summary>
public class AnimationLayerTests
{
    private const float Quantization = 2e-3f;

    private static RawAnimation KneeDelta(ref SkeletonBlob skeleton, Quaternion extra)
    {
        var rest = skeleton.RestPoses.ToArray();
        var source = new RawAnimation { Name = "knee", Duration = 1f };
        for (var joint = 0; joint < rest.Length; joint++) source.Tracks.Add(new RawTrack());
        source.Tracks[1].Rotations.Add(new RotationKey(0f, rest[1].Rotation * extra));
        return source;
    }

    [Test]
    public async Task an_override_layer_replaces_only_the_joints_its_mask_admits()
    {
        using var skeleton = TestRigs.Chain();
        using var walk = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var straight = TestRigs.KneeAt(ref skeleton.Value, Quaternion.Identity);
        var knee = JointMask.Branch(ref skeleton.Value, "knee");
        using var player = new AnimationPlayer(skeleton);
        player.Add(walk);
        var upper = player.AddLayer(AnimationLayerMode.Override, 1f, knee);
        var reach = player.Add(straight, upper);

        player.Evaluate();
        var full = (player.LocalPose[0].Translation.X, player.LocalPose[1].Rotation);
        player.SetWeight(upper, 0.5f);
        player.Evaluate();
        var half = player.LocalPose[1].Rotation;
        player.SetMask(upper, JointMask.Create(ref skeleton.Value, [0f, 0f, 0f]));
        player.Evaluate();
        var excluded = (player.LocalPose[0].Translation.X, player.LocalPose[1].Rotation);
        player.SetMask(upper, JointMask.Create(ref skeleton.Value, [0f, 0.5f, 0f]));
        player.SetWeight(upper, 1f);
        player.Evaluate();
        var soft = player.LocalPose[1].Rotation;
        player.SetMask(upper, null);
        player.Evaluate();
        var opaque = (player.LocalPose[0].Translation.X, player.LocalPose[1].Rotation);
        player.SetWeight(reach, 0f);
        player.Evaluate();
        var empty = (player.LocalPose[0].Translation.X, player.LocalPose[1].Rotation);

        var halfway = Quaternion.Normalize(Quaternion.Lerp(TestRigs.QuarterTurnZ, Quaternion.Identity, 0.5f));
        // The mask admits only the knee: the hip stays the walk's.
        await Assert.That(full.X).IsEqualTo(10f).Within(Quantization);
        await Assert.That(TestRigs.Angle(full.Rotation, Quaternion.Identity)).IsLessThan(Quantization);
        await Assert.That(TestRigs.Angle(half, halfway)).IsLessThan(Quantization);
        // An all-zero mask is a valid mask that admits nothing.
        await Assert.That(excluded.X).IsEqualTo(10f).Within(Quantization);
        await Assert.That(TestRigs.Angle(excluded.Rotation, TestRigs.QuarterTurnZ)).IsLessThan(Quantization);
        await Assert.That(TestRigs.Angle(soft, halfway)).IsLessThan(Quantization);
        // Unmasked at full weight, the layer hides the walk entirely: its own hip is at rest.
        await Assert.That(opaque.X).IsEqualTo(0f).Within(Quantization);
        await Assert.That(TestRigs.Angle(opaque.Rotation, Quaternion.Identity)).IsLessThan(Quantization);
        // A layer with nothing to show leaves the pose beneath, not the rest pose.
        await Assert.That(empty.X).IsEqualTo(10f).Within(Quantization);
        await Assert.That(TestRigs.Angle(empty.Rotation, TestRigs.QuarterTurnZ)).IsLessThan(Quantization);
    }

    [Test]
    public async Task an_additive_layer_applies_deltas_over_the_pose_beneath()
    {
        using var skeleton = TestRigs.Chain();
        var extra = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
        var rest = skeleton.Value.RestPoses.ToArray();
        var source = KneeDelta(ref skeleton.Value, extra);
        source.Tracks[0].Translations.Add(new TranslationKey(0f, rest[0].Translation + new Vector3(2f, 0f, 0f)));
        source.Tracks[0].Scales.Add(new ScaleKey(0f, new Vector3(1.5f, 1f, 1f)));
        using var lean = AdditiveAnimationBuilder.Build(source, rest);
        // Against its own keys a constant clip is all identity deltas.
        using var still = AdditiveAnimationBuilder.Build(source);
        using var walk = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var player = new AnimationPlayer(skeleton);
        player.Add(walk);
        var nothing = player.AddLayer(AnimationLayerMode.Additive);
        player.Add(still, nothing);
        player.Evaluate();
        var unchanged = player.LocalPose.ToArray();
        var layer = player.AddLayer(AnimationLayerMode.Additive);
        player.Add(lean, layer);
        player.Evaluate();
        var full = player.LocalPose.ToArray();
        player.SetWeight(layer, 0.5f);
        player.Evaluate();
        var half = player.LocalPose.ToArray();

        await Assert.That(unchanged[0].Translation.X).IsEqualTo(10f).Within(Quantization);
        await Assert.That(TestRigs.Angle(unchanged[1].Rotation, TestRigs.QuarterTurnZ)).IsLessThan(Quantization);
        await Assert.That(Vector3.Distance(full[0].Translation, new Vector3(12f, 1f, 0f))).IsLessThan(Quantization);
        await Assert.That(Vector3.Distance(full[0].Scale, new Vector3(1.5f, 1f, 1f))).IsLessThan(Quantization);
        await Assert.That(TestRigs.Angle(full[1].Rotation, TestRigs.QuarterTurnZ * extra)).IsLessThan(Quantization);
        await Assert.That(Vector3.Distance(half[0].Translation, new Vector3(11f, 1f, 0f))).IsLessThan(Quantization);
        await Assert.That(Vector3.Distance(half[0].Scale, new Vector3(1.25f, 1f, 1f))).IsLessThan(Quantization);
        await Assert.That(TestRigs.Angle(half[1].Rotation, TestRigs.QuarterTurnZ * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4f))).IsLessThan(Quantization);
    }

    [Test]
    public async Task additive_layers_apply_in_order()
    {
        using var skeleton = TestRigs.Chain();
        var aboutX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
        var aboutY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var rest = skeleton.Value.RestPoses.ToArray();
        using var turnX = AdditiveAnimationBuilder.Build(KneeDelta(ref skeleton.Value, aboutX), rest);
        using var turnY = AdditiveAnimationBuilder.Build(KneeDelta(ref skeleton.Value, aboutY), rest);
        using var walk = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var xThenY = new AnimationPlayer(skeleton);
        using var yThenX = new AnimationPlayer(skeleton);
        foreach (var (player, first, second) in new[] { (xThenY, turnX, turnY), (yThenX, turnY, turnX) })
        {
            player.Add(walk);
            player.Add(first, player.AddLayer(AnimationLayerMode.Additive));
            player.Add(second, player.AddLayer(AnimationLayerMode.Additive));
            player.Evaluate();
        }

        var expectedXY = TestRigs.QuarterTurnZ * aboutX * aboutY;
        var expectedYX = TestRigs.QuarterTurnZ * aboutY * aboutX;
        await Assert.That(TestRigs.Angle(xThenY.LocalPose[1].Rotation, expectedXY)).IsLessThan(Quantization);
        await Assert.That(TestRigs.Angle(yThenX.LocalPose[1].Rotation, expectedYX)).IsLessThan(Quantization);
        await Assert.That(TestRigs.Angle(expectedXY, expectedYX)).IsGreaterThan(1f);
    }

    [Test]
    public async Task clips_masks_and_layers_refuse_what_does_not_fit_them()
    {
        using var skeleton = TestRigs.Chain();
        var (rawOther, _) = TestRigs.Parity(joints: 3);
        using var otherSkeleton = SkeletonBuilder.Build(rawOther);
        using var walk = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var delta = AdditiveAnimationBuilder.Build(ref walk.Value);
        using var player = new AnimationPlayer(skeleton);
        var additive = player.AddLayer(AnimationLayerMode.Additive);
        var foreignMask = JointMask.Branch(ref otherSkeleton.Value, "j0");

        var absoluteAsDelta = await Assert.That(() => player.Add(walk, additive)).Throws<ArgumentException>();
        await Assert.That(absoluteAsDelta!.Message).Contains("additive clips");
        await Assert.That(() => player.Add(delta, player.BaseLayer)).Throws<ArgumentException>();
        await Assert.That(() => player.Play(delta, player.BaseLayer)).Throws<ArgumentException>();
        var alienMask = await Assert.That(() => player.AddLayer(AnimationLayerMode.Override, 1f, foreignMask)).Throws<ArgumentException>();
        await Assert.That(alienMask!.Message).Contains("another skeleton");
        await Assert.That(() => player.SetMask(additive, foreignMask)).Throws<ArgumentException>();
        await Assert.That(() => player.AddLayer((AnimationLayerMode)7)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => JointMask.Create(ref skeleton.Value, [1f, 1f])).Throws<ArgumentException>();
        await Assert.That(() => JointMask.Create(ref skeleton.Value, [0f, 1.5f, 0f])).Throws<ArgumentException>();
        await Assert.That(() => JointMask.Branch(ref skeleton.Value, "tail")).Throws<ArgumentException>();
        await Assert.That(player.Layers.Length).IsEqualTo(2);
        await Assert.That(player.GetState(additive).Mask).IsNull();
    }

    [Test]
    public async Task a_layer_weight_fades_and_removing_a_layer_removes_its_playbacks()
    {
        using var skeleton = TestRigs.Chain();
        using var straight = TestRigs.KneeAt(ref skeleton.Value, Quaternion.Identity);
        using var player = new AnimationPlayer(skeleton);
        var upper = player.AddLayer(AnimationLayerMode.Override, 0f);
        var reach = player.Add(straight, upper);
        player.FadeWeight(upper, 1f, 1f);
        player.Advance(0.25f);
        var fading = player.GetState(upper);
        player.Remove(upper);
        var next = player.AddLayer(AnimationLayerMode.Override);

        await Assert.That(fading.Weight).IsEqualTo(0.25f).Within(1e-6f);
        await Assert.That(fading.TargetWeight).IsEqualTo(1f);
        await Assert.That(fading.IsFading).IsTrue();
        await Assert.That(player.Contains(upper)).IsFalse();
        await Assert.That(player.Contains(reach)).IsFalse();
        await Assert.That(next).IsNotEqualTo(upper);
        await Assert.That(player.Layers.ToArray()).IsEquivalentTo(new[] { player.BaseLayer, next });
    }
}
