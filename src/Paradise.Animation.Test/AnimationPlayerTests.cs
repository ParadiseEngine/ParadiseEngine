using System.Numerics;

using Paradise.Animation.Offline;
using Paradise.BLOB;

namespace Paradise.Animation.Test;

/// <summary>Playback state: time wraps or clamps by rate, a fade blends the outgoing clip out over its own clock, and evaluation is the sampler plus the hierarchy walk with nothing allocated per frame.</summary>
public class AnimationPlayerTests
{
    private const float Quantization = 2e-3f;

    /// <summary>Two one-second clips on the chain: "rise" lifts the hip 0→1 on Y; "turn" swings the knee to a quarter turn.</summary>
    private static (NativeBlobAssetReference<SkeletonBlob> Skeleton, NativeBlobAssetReference<AnimationBlob> Rise, NativeBlobAssetReference<AnimationBlob> Turn) Clips()
    {
        var skeleton = TestRigs.Chain();
        var rise = new ClipData("rise", [new ClipChannelData(0, ChannelPath.Translation, false, [0f, 1f], [0, 1, 0, 0, 2, 0])]);
        var turn = new ClipData("turn", [new ClipChannelData(1, ChannelPath.Rotation, false, [0f, 1f], [0, 0, 0.7071068f, 0.7071068f, 0, 0, 0, 1])]);
        return (skeleton,
            AnimationBuilder.Build(ClipConverter.ToRaw(rise, ref skeleton.Value)),
            AnimationBuilder.Build(ClipConverter.ToRaw(turn, ref skeleton.Value)));
    }

    [Test]
    public async Task a_looping_clip_wraps_and_a_one_shot_clamps_and_finishes()
    {
        var (skeleton, rise, turn) = Clips();
        using var _ = skeleton;
        using var __ = rise;
        using var ___ = turn;
        using var player = new AnimationPlayer(skeleton);

        player.Play(rise, loop: true, rate: 2f);
        player.Advance(0.75f);
        var wrapped = player.Time;
        player.Play(rise, loop: false);
        player.Advance(5f);

        await Assert.That(wrapped).IsEqualTo(0.5f).Within(1e-6f);
        await Assert.That(player.Time).IsEqualTo(1f);
        await Assert.That(player.IsFinished).IsTrue();
        await Assert.That(player.Current).IsEqualTo(rise);
        player.Evaluate();
        await Assert.That(Vector3.Distance(player.LocalPose[0].Translation, new Vector3(0, 2, 0))).IsLessThan(Quantization);
        await Assert.That(player.ModelMatrices[1].Translation.Y).IsEqualTo(2f).Within(Quantization);
    }

    [Test]
    public async Task evaluate_is_the_sampler_and_the_hierarchy_walk()
    {
        var (skeleton, rise, turn) = Clips();
        using var _ = skeleton;
        using var __ = rise;
        using var ___ = turn;
        using var player = new AnimationPlayer(skeleton);
        using var context = SamplingContext.Create(skeleton.Value.JointCount);
        using var expected = JointPoses.Create(skeleton.Value.JointCount);
        var models = new Matrix4x4[skeleton.Value.JointCount];

        player.Play(turn);
        player.Advance(0.3f);
        player.Evaluate();
        context.Value.Sample(ref turn.Value, 0.3f, ref expected.Value);
        LocalToModel.Compute(ref skeleton.Value, ref expected.Value, models);

        await Assert.That(player.LocalPose.ToArray()).IsEquivalentTo(expected.Value.ToArray());
        await Assert.That(player.ModelMatrices.ToArray()).IsEquivalentTo(models);
    }

    [Test]
    public async Task a_fade_blends_the_outgoing_clip_out_on_its_own_clock_then_drops_it()
    {
        var (skeleton, rise, turn) = Clips();
        using var _ = skeleton;
        using var __ = rise;
        using var ___ = turn;
        using var player = new AnimationPlayer(skeleton);

        player.Play(rise, loop: false);
        player.Advance(1f);                      // hip at y=2
        player.Play(turn, fadeSeconds: 0.5f, loop: false, rate: 0f, startTime: 1f); // turn held at its end: knee at identity, hip at rest y=1
        player.Advance(0.25f);
        player.Evaluate();

        await Assert.That(player.IsFading).IsTrue();
        await Assert.That(player.FadeProgress).IsEqualTo(0.5f).Within(1e-6f);
        // Half-way: hip half-way from 2 (rise, clamped at its end) to 1 (turn's rest), knee half-way from its rest quarter turn to identity.
        await Assert.That(player.LocalPose[0].Translation.Y).IsEqualTo(1.5f).Within(Quantization);
        var halfTurn = Quaternion.Normalize(Quaternion.Lerp(TestRigs.QuarterTurnZ, Quaternion.Identity, 0.5f));
        await Assert.That(Quaternion.Dot(player.LocalPose[1].Rotation, halfTurn)).IsGreaterThan(1f - Quantization);

        player.Advance(0.3f);
        player.Evaluate();
        await Assert.That(player.IsFading).IsFalse();
        await Assert.That(player.FadeProgress).IsEqualTo(1f);
        await Assert.That(player.LocalPose[0].Translation.Y).IsEqualTo(1f).Within(Quantization);
    }

    [Test]
    public async Task stop_returns_to_rest_and_a_foreign_clip_is_refused()
    {
        var (skeleton, rise, turn) = Clips();
        using var _ = skeleton;
        using var __ = rise;
        using var ___ = turn;
        using var player = new AnimationPlayer(skeleton);
        var raw = new RawAnimation { Name = "foreign", Duration = 1f };
        raw.Tracks.Add(new RawTrack());
        using var foreign = AnimationBuilder.Build(raw);

        player.Play(rise);
        player.Advance(0.5f);
        player.Stop();
        player.Evaluate();

        await Assert.That(player.Current).IsNull();
        await Assert.That(player.LocalPose.ToArray()).IsEquivalentTo(skeleton.Value.RestPoses.ToArray());
        var error = await Assert.That(() => player.Play(foreign)).Throws<ArgumentException>();
        await Assert.That(error!.Message).Contains("1 tracks");
    }

    [Test]
    public async Task the_palette_follows_the_skin_and_the_mesh_joint()
    {
        var (skeleton, rise, turn) = Clips();
        using var _ = skeleton;
        using var __ = rise;
        using var ___ = turn;
        using var player = new AnimationPlayer(skeleton);
        player.Play(rise, loop: false);
        player.Advance(1f);
        player.Evaluate();
        var palette = new Matrix4x4[2];
        var inverseBinds = new[] { Matrix4x4.CreateTranslation(0, -1, 0), Matrix4x4.Identity };

        // Slots name knee then hip; the mesh hangs off "prop" (joint 2), which stays at the origin.
        SkinningPalette.Compute(player.ModelMatrices, [1, 0], inverseBinds, meshJoint: 2, palette);

        // "prop" rests at identity, but its quantized rotation is identity only to 1e-5, so the inverse is not exactly I.
        var models = player.ModelMatrices.ToArray();
        Matrix4x4.Invert(models[2], out var inverseProp);
        await Assert.That(TestRigs.MaxAbs(palette[0] - inverseBinds[0] * models[1] * inverseProp)).IsLessThan(1e-6f);
        await Assert.That(TestRigs.MaxAbs(palette[1] - models[0] * inverseProp)).IsLessThan(1e-6f);
        await Assert.That(() => SkinningPalette.Compute(models, [7], [Matrix4x4.Identity], -1, palette)).Throws<ArgumentException>();
    }

    [Test]
    public async Task a_frame_allocates_nothing()
    {
        var (skeleton, rise, turn) = Clips();
        using var _ = skeleton;
        using var __ = rise;
        using var ___ = turn;
        using var player = new AnimationPlayer(skeleton);
        var palette = new Matrix4x4[2];
        player.Play(rise);
        player.Play(turn, fadeSeconds: 10f);
        player.Advance(0.01f);
        player.Evaluate();

        var joints = new[] { 0, 1 };
        var inverseBinds = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 100; frame++)
        {
            player.Advance(0.016f);
            player.Evaluate();
        }

        var afterFrames = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 100; frame++) SkinningPalette.Compute(player.ModelMatrices, joints, inverseBinds, 2, palette);
        var afterPalette = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(afterFrames - before).IsEqualTo(0L);
        await Assert.That(afterPalette - afterFrames).IsEqualTo(0L);
    }
}
