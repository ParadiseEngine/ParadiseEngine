using TUnit.Assertions.Enums;
using System.Numerics;
using System.Runtime.CompilerServices;

using Paradise.Animation.Offline;
using Paradise.BLOB;

namespace Paradise.Animation.Test;

/// <summary>Playbacks keep their own clocks and blend by relative weight; fades run on elapsed time and continue from wherever an interruption finds them; handles never outlive their playback; a frame allocates nothing.</summary>
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

    private static float HipX(AnimationPlayer player) => player.LocalPose[0].Translation.X;

    private static unsafe nint Address(ref JointPoses poses) => (nint)Unsafe.AsPointer(ref poses);

    [Test]
    public async Task a_looping_clip_wraps_and_a_one_shot_clamps_and_holds_its_end()
    {
        var (skeleton, rise, turn) = Clips();
        using var _ = skeleton;
        using var __ = rise;
        using var ___ = turn;
        using var player = new AnimationPlayer(skeleton);

        var looping = player.Play(rise, loop: true, rate: 2f);
        player.Advance(0.75f);
        var wrapped = player.GetState(looping).Time;
        var once = player.Play(rise, loop: false);
        player.Advance(5f);
        var state = player.GetState(once);
        player.Evaluate();

        await Assert.That(wrapped).IsEqualTo(0.5f).Within(1e-6f);
        await Assert.That(player.Contains(looping)).IsFalse();
        await Assert.That(state.Time).IsEqualTo(1f);
        await Assert.That(state.IsFinished).IsTrue();
        await Assert.That(Vector3.Distance(player.LocalPose[0].Translation, new Vector3(0, 2, 0))).IsLessThan(Quantization);
        await Assert.That(player.ModelMatrices[1].Translation.Y).IsEqualTo(2f).Within(Quantization);
    }

    [Test]
    public async Task each_playback_clock_rewinds_pauses_and_seeks_alone()
    {
        using var skeleton = TestRigs.Chain();
        using var ramp = TestRigs.HipRamp(ref skeleton.Value, 1f);
        using var player = new AnimationPlayer(skeleton);

        var backwards = player.Add(ramp, rate: -1f, time: 0.25f);
        var oneShot = player.Add(ramp, weight: 0f, loop: false, rate: -1f);
        player.Advance(2.5f);
        var rewound = player.GetState(backwards).Time;
        var heldAtStart = player.GetState(oneShot);
        player.Pause(backwards);
        player.Advance(0.5f);
        var paused = player.GetState(backwards).Time;
        player.Resume(backwards);
        player.SetRate(backwards, 1f);
        player.Advance(0.125f);
        var resumed = player.GetState(backwards).Time;
        player.Seek(backwards, 1.25f);
        var seekWrapped = player.GetState(backwards).Time;
        player.Seek(oneShot, 3f);
        var seekClamped = player.GetState(oneShot).Time;
        player.Evaluate();
        var first = HipX(player);
        player.Evaluate();
        var second = HipX(player);

        // 0.25 − 2.5 wraps to 0.75; the one-shot playing backwards stops at its start.
        await Assert.That(rewound).IsEqualTo(0.75f).Within(1e-5f);
        await Assert.That(heldAtStart.Time).IsEqualTo(0f);
        await Assert.That(heldAtStart.IsFinished).IsTrue();
        await Assert.That(paused).IsEqualTo(0.75f).Within(1e-5f);
        await Assert.That(resumed).IsEqualTo(0.875f).Within(1e-5f);
        await Assert.That(seekWrapped).IsEqualTo(0.25f).Within(1e-5f);
        await Assert.That(seekClamped).IsEqualTo(1f);
        await Assert.That(first).IsEqualTo(0.25f).Within(Quantization);
        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task a_step_that_overflows_leaves_a_loop_in_range_and_a_one_shot_at_its_end()
    {
        using var skeleton = TestRigs.Chain();
        using var ramp = TestRigs.HipRamp(ref skeleton.Value, 1f);
        using var player = new AnimationPlayer(skeleton);

        // float.MaxValue × 10 s overflows to infinity, which a loop cannot wrap and a one-shot clamps to its end.
        var looping = player.Add(ramp, rate: float.MaxValue);
        var oneShot = player.Add(ramp, weight: 0f, loop: false, rate: float.MaxValue);
        player.Advance(10f);
        var wrapped = player.GetState(looping).Time;
        var ended = player.GetState(oneShot);
        player.Evaluate();

        await Assert.That(wrapped >= 0f && wrapped < 1f).IsTrue();
        await Assert.That(ended.Time).IsEqualTo(1f);
        await Assert.That(ended.IsFinished).IsTrue();
        await Assert.That(float.IsFinite(HipX(player))).IsTrue();
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

        await Assert.That(player.LocalPose.ToArray()).IsEquivalentTo(expected.Value.ToArray(), CollectionOrdering.Matching);
        await Assert.That(player.ModelMatrices.ToArray()).IsEquivalentTo(models, CollectionOrdering.Matching);
    }

    [Test]
    public async Task any_number_of_playbacks_blend_by_relative_weight()
    {
        using var skeleton = TestRigs.Chain();
        using var at0 = TestRigs.HipAt(ref skeleton.Value, 0f);
        using var at10 = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var at20 = TestRigs.HipAt(ref skeleton.Value, 20f);
        using var player = new AnimationPlayer(skeleton);

        player.Evaluate();
        var nothing = player.LocalPose.ToArray();
        var a = player.Add(at0, 0.2f);
        var b = player.Add(at10, 0.3f);
        var c = player.Add(at20, 0.5f);
        player.Evaluate();
        var three = HipX(player);
        player.SetWeight(a, 2f);
        player.SetWeight(b, 3f);
        player.SetWeight(c, 5f);
        player.Evaluate();
        var scaled = HipX(player);
        for (var i = 0; i < 16; i++) player.Add(at20, 1f);
        player.Evaluate();
        var nineteen = HipX(player);
        foreach (var playback in player.Playbacks.ToArray())
        {
            if (playback != b) player.Remove(playback);
        }

        player.Evaluate();
        var one = HipX(player);

        await Assert.That(nothing).IsEquivalentTo(skeleton.Value.RestPoses.ToArray(), CollectionOrdering.Matching);
        await Assert.That(three).IsEqualTo(13f).Within(1e-3f);
        await Assert.That(scaled).IsEqualTo(13f).Within(1e-3f);
        // (2×0 + 3×10 + 5×20 + 16×20) / 26.
        await Assert.That(nineteen).IsEqualTo(450f / 26f).Within(1e-3f);
        await Assert.That(one).IsEqualTo(10f).Within(1e-3f);
    }

    [Test]
    public async Task two_playbacks_of_one_clip_keep_their_own_clocks()
    {
        using var skeleton = TestRigs.Chain();
        using var ramp = TestRigs.HipRamp(ref skeleton.Value, 1f);
        using var player = new AnimationPlayer(skeleton);

        var early = player.Add(ramp, 1f, time: 0.25f);
        var late = player.Add(ramp, 0f, time: 0.75f);
        player.Evaluate();
        var earlyX = HipX(player);
        player.SetWeight(early, 0f);
        player.SetWeight(late, 1f);
        player.Evaluate();
        var lateX = HipX(player);
        player.Advance(0.125f);
        player.Evaluate();

        await Assert.That(earlyX).IsEqualTo(0.25f).Within(Quantization);
        await Assert.That(lateX).IsEqualTo(0.75f).Within(Quantization);
        await Assert.That(HipX(player)).IsEqualTo(0.875f).Within(Quantization);
        await Assert.That(player.GetState(early).Time).IsEqualTo(0.375f).Within(1e-6f);
    }

    [Test]
    public async Task an_interrupted_cross_fade_continues_from_the_pose_it_reached()
    {
        using var skeleton = TestRigs.Chain();
        using var at0 = TestRigs.HipAt(ref skeleton.Value, 0f);
        using var at10 = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var at20 = TestRigs.HipAt(ref skeleton.Value, 20f);
        using var player = new AnimationPlayer(skeleton);

        var a = player.Play(at0);
        var b = player.Play(at10, 1f);
        player.Advance(0.25f);
        player.Evaluate();
        var before = HipX(player);
        var c = player.Play(at20, 1f);
        player.Evaluate();
        var interrupted = HipX(player);
        player.Advance(0.25f);
        player.Evaluate();
        var continued = HipX(player);
        var fading = player.Playbacks.Length;
        player.Advance(1f);
        player.Evaluate();

        await Assert.That(before).IsEqualTo(2.5f).Within(1e-3f);
        // The two-slot player dropped a's quarter and jumped to 10 here.
        await Assert.That(interrupted).IsEqualTo(2.5f).Within(1e-3f);
        // a keeps fading on its own schedule (0.5 now); b, which was fading in, fades out from 0.25 (0.1875 now); c rises to 0.25.
        await Assert.That(continued).IsEqualTo((0.1875f * 10f + 0.25f * 20f) / (0.5f + 0.1875f + 0.25f)).Within(1e-3f);
        await Assert.That(fading).IsEqualTo(3);
        await Assert.That(HipX(player)).IsEqualTo(20f).Within(1e-3f);
        await Assert.That(player.Playbacks.ToArray()).IsEquivalentTo(new[] { c }, CollectionOrdering.Matching);
        await Assert.That(player.Contains(a) || player.Contains(b)).IsFalse();
    }

    [Test]
    public async Task cross_fades_restarted_faster_than_they_finish_keep_a_bounded_set_of_playbacks()
    {
        using var skeleton = TestRigs.Chain();
        using var at0 = TestRigs.HipAt(ref skeleton.Value, 0f);
        using var at10 = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var player = new AnimationPlayer(skeleton);

        player.Play(at0);
        var most = 0;
        var last = default(PlaybackHandle);
        var lastX = 0f;
        for (var frame = 0; frame < 600; frame++)
        {
            // A cross-fade of 18 frames restarted every 7: none finishes before the next begins.
            if (frame % 7 == 0)
            {
                lastX = frame % 14 == 0 ? 10f : 0f;
                last = player.Play(lastX == 10f ? at10 : at0, 0.3f);
            }

            player.Advance(1f / 60f);
            most = Math.Max(most, player.Playbacks.Length);
        }

        player.Advance(1f);
        player.Evaluate();

        // Each outgoing playback is gone within one fade of leaving: three fading out at most, plus the incoming one.
        await Assert.That(most).IsEqualTo(4);
        await Assert.That(player.Playbacks.ToArray()).IsEquivalentTo(new[] { last }, CollectionOrdering.Matching);
        await Assert.That(HipX(player)).IsEqualTo(lastX).Within(1e-3f);
    }

    [Test]
    public async Task an_outgoing_playback_keeps_advancing_until_it_is_removed()
    {
        using var skeleton = TestRigs.Chain();
        using var ramp = TestRigs.HipRamp(ref skeleton.Value, 1f, duration: 4f);
        using var player = new AnimationPlayer(skeleton);

        var outgoing = player.Play(ramp);
        player.Play(ramp, 1f);
        player.Advance(0.5f);
        var halfway = player.GetState(outgoing);
        player.Advance(0.5f);

        await Assert.That(halfway.Time).IsEqualTo(0.5f).Within(1e-6f);
        await Assert.That(halfway.Weight).IsEqualTo(0.5f).Within(1e-6f);
        await Assert.That(halfway.IsFadingOut).IsTrue();
        await Assert.That(player.Contains(outgoing)).IsFalse();
    }

    [Test]
    public async Task fades_run_on_elapsed_time_and_land_on_their_targets()
    {
        using var skeleton = TestRigs.Chain();
        using var at10 = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var at20 = TestRigs.HipAt(ref skeleton.Value, 20f);
        using var player = new AnimationPlayer(skeleton);

        var fast = player.Add(at10, 0f, rate: 5f);
        var paused = player.Add(at20, 1f);
        player.Pause(paused);
        player.FadeWeight(fast, 1f, 1f);
        player.FadeWeight(paused, 0f, 2f);
        player.Advance(0.25f);
        var quarter = (player.GetState(fast).Weight, player.GetState(paused).Weight);
        player.Advance(10f);
        var landed = player.GetState(fast);
        player.FadeWeight(fast, 0f, 1f);
        player.Advance(0.5f);
        player.SetWeight(fast, 0.8f);
        player.Advance(1f);
        var held = player.GetState(fast);
        player.FadeWeight(fast, 0.2f, 0f);
        var immediate = player.GetState(fast).Weight;

        // The rate scales the clock, not the fade; pausing stops the clock, not the fade.
        await Assert.That(quarter.Item1).IsEqualTo(0.25f).Within(1e-6f);
        await Assert.That(quarter.Item2).IsEqualTo(0.875f).Within(1e-6f);
        await Assert.That(landed.Weight).IsEqualTo(1f);
        await Assert.That(landed.IsFading).IsFalse();
        await Assert.That(player.GetState(paused).Weight).IsEqualTo(0f);
        await Assert.That(player.Contains(paused)).IsTrue();
        await Assert.That(held.Weight).IsEqualTo(0.8f);
        await Assert.That(held.IsFading).IsFalse();
        await Assert.That(immediate).IsEqualTo(0.2f);
    }

    [Test]
    public async Task stop_fades_to_rest_and_play_reverses_it_without_a_jump()
    {
        using var skeleton = TestRigs.Chain();
        using var at10 = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var at20 = TestRigs.HipAt(ref skeleton.Value, 20f);
        using var player = new AnimationPlayer(skeleton);

        var first = player.Play(at10, 1f);
        player.Advance(0.5f);
        player.Evaluate();
        var fadingIn = HipX(player);
        player.Advance(0.5f);
        player.Stop(1f);
        player.Advance(0.5f);
        player.Evaluate();
        var fadingOut = HipX(player);
        var second = player.Play(at20, 1f);
        player.Evaluate();
        var interrupted = HipX(player);
        player.Advance(0.5f);
        player.Evaluate();
        var reversing = HipX(player);
        player.Advance(1f);
        player.Evaluate();
        var settled = HipX(player);
        player.Stop();
        player.Evaluate();

        // Nothing showed, so the first clip fades in from rest (X = 0) rather than popping in.
        await Assert.That(fadingIn).IsEqualTo(5f).Within(1e-3f);
        await Assert.That(fadingOut).IsEqualTo(5f).Within(1e-3f);
        await Assert.That(interrupted).IsEqualTo(5f).Within(1e-3f);
        // The layer is back to 0.75 of a half-and-half mix of 10 and 20.
        await Assert.That(reversing).IsEqualTo(11.25f).Within(1e-3f);
        await Assert.That(settled).IsEqualTo(20f).Within(1e-3f);
        await Assert.That(player.Contains(first)).IsFalse();
        await Assert.That(player.Contains(second)).IsFalse();
        await Assert.That(player.Playbacks.Length).IsEqualTo(0);
        await Assert.That(player.LocalPose.ToArray()).IsEquivalentTo(skeleton.Value.RestPoses.ToArray(), CollectionOrdering.Matching);
        await Assert.That(player.GetState(player.BaseLayer).EffectiveWeight).IsEqualTo(1f);
    }

    [Test]
    public async Task handles_never_outlive_or_redirect_their_playback()
    {
        using var skeleton = TestRigs.Chain();
        using var at10 = TestRigs.HipAt(ref skeleton.Value, 10f);
        using var player = new AnimationPlayer(skeleton);
        using var other = new AnimationPlayer(skeleton);

        var first = player.Add(at10);
        player.Remove(first);
        var second = player.Add(at10, 0.5f);
        var foreign = other.Add(at10);

        await Assert.That(player.Contains(first)).IsFalse();
        await Assert.That(player.Contains(second)).IsTrue();
        await Assert.That(player.Contains(default(PlaybackHandle))).IsFalse();
        var stale = await Assert.That(() => player.SetWeight(first, 1f)).Throws<ArgumentException>();
        await Assert.That(stale!.Message).Contains("removed");
        var alien = await Assert.That(() => player.GetState(foreign)).Throws<ArgumentException>();
        await Assert.That(alien!.Message).Contains("another player");
        var none = await Assert.That(() => player.Remove(default(PlaybackHandle))).Throws<ArgumentException>();
        await Assert.That(none!.Message).Contains("default");
        await Assert.That(() => player.Remove(player.BaseLayer)).Throws<InvalidOperationException>();
        await Assert.That(player.GetState(second).Weight).IsEqualTo(0.5f);
    }

    [Test]
    public async Task storage_grows_without_moving_the_output_or_disturbing_playbacks()
    {
        using var skeleton = TestRigs.Chain();
        using var ramp = TestRigs.HipRamp(ref skeleton.Value, 8f);
        using var player = new AnimationPlayer(skeleton, playbackCapacity: 1);

        var early = player.Add(ramp, time: 0.25f);
        player.Evaluate();
        var before = Address(ref player.LocalPose);
        var added = new List<PlaybackHandle>();
        for (var i = 0; i < 7; i++) added.Add(player.Add(ramp, 0f, time: i / 8f));
        player.Evaluate();
        var after = Address(ref player.LocalPose);

        await Assert.That(after).IsEqualTo(before);
        await Assert.That(player.GetState(early).Time).IsEqualTo(0.25f);
        await Assert.That(HipX(player)).IsEqualTo(2f).Within(Quantization);
        await Assert.That(added.All(handle => player.Contains(handle))).IsTrue();
    }

    [Test]
    public async Task invalid_arguments_leave_the_player_unchanged()
    {
        using var skeleton = TestRigs.Chain();
        using var ramp = TestRigs.HipRamp(ref skeleton.Value, 1f);
        using var player = new AnimationPlayer(skeleton);
        var playback = player.Add(ramp, 0.5f);

        await Assert.That(() => player.Add(ramp, -1f)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => player.Add(ramp, float.NaN)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => player.SetWeight(playback, float.PositiveInfinity)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => player.FadeWeight(playback, 1f, -1f)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => player.CrossFade(playback, float.NaN)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => player.SetRate(playback, float.NaN)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => player.Seek(playback, float.PositiveInfinity)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => player.Advance(-1f)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => player.Advance(float.NaN)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => player.AddLayer(AnimationLayerMode.Override, 1.5f)).Throws<ArgumentOutOfRangeException>();

        var state = player.GetState(playback);
        await Assert.That(player.Playbacks.Length).IsEqualTo(1);
        await Assert.That(player.Layers.Length).IsEqualTo(1);
        await Assert.That(state.Weight).IsEqualTo(0.5f);
        await Assert.That(state.Rate).IsEqualTo(1f);
        await Assert.That(state.Time).IsEqualTo(0f);
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

        await Assert.That(player.Playbacks.Length).IsEqualTo(0);
        await Assert.That(player.LocalPose.ToArray()).IsEquivalentTo(skeleton.Value.RestPoses.ToArray(), CollectionOrdering.Matching);
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
        using var additive = AdditiveAnimationBuilder.Build(ref turn.Value);
        var knee = JointMask.Branch(ref skeleton.Value, "knee");
        using var player = new AnimationPlayer(skeleton);
        player.Reserve(playbacks: 16, layers: 3, syncGroups: 1);
        var upper = player.AddLayer(AnimationLayerMode.Override, 0.5f, knee);
        var add = player.AddLayer(AnimationLayerMode.Additive);
        var group = player.AddSyncGroup();
        var walk = player.Add(rise);
        var run = player.Add(rise, 0.5f, rate: 1.5f);
        var third = player.Add(turn, 0.25f);
        player.Synchronize(walk, group);
        player.Synchronize(run, group);
        player.Play(turn, upper);
        player.Add(additive, add, 0.7f);
        var palette = new Matrix4x4[2];
        var joints = new[] { 0, 1 };
        var inverseBinds = new[] { Matrix4x4.Identity, Matrix4x4.Identity };

        void Frames(int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                player.SetWeight(run, frame % 10 / 10f);
                player.FadeWeight(walk, 1f - frame % 7 / 7f, 0.1f);
                player.FadeWeight(upper, frame % 2, 0.05f);
                if (frame % 3 == 0) player.CrossFade(third, 0.2f);
                // Each Play replaces the layer's playback: the one it fades out is removed in time, so storage never grows.
                if (frame % 20 == 0) player.Play(turn, upper, 0.1f);
                player.Advance(0.016f);
                player.Evaluate();
                SkinningPalette.Compute(player.ModelMatrices, joints, inverseBinds, 2, palette);
            }
        }

        Frames(100);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Frames(200);
        var after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0L);
    }
}
