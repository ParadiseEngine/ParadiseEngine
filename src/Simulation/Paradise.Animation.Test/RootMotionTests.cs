using System.Numerics;

using Paradise.Animation.Offline;
using Paradise.BLOB;

namespace Paradise.Animation.Test;

/// <summary>A root-motion clip hands its root joint's ground travel to the host and poses that joint over the actor, so the body moves once; travel wraps with a loop, follows the rate, blends like the pose and repeats bit for bit.</summary>
public class RootMotionTests
{
    private const int Root = 1;
    private const int Pelvis = 2;
    private const float Exact = 1e-5f;

    /// <summary>What ozz's 15-bit rotation keys leave on a rest rotation, a metre or two out.</summary>
    private const float Quantization = 1e-3f;

    /// <summary>armature (turned and scaled as given) → root at its origin → pelvis a metre up.</summary>
    private static NativeBlobAssetReference<SkeletonBlob> Walker(Quaternion? turn = null, float scale = 1f)
    {
        var raw = new RawSkeleton();
        var armature = new RawJoint("armature") { Transform = new JointPose(Vector3.Zero, turn ?? Quaternion.Identity, new Vector3(scale)) };
        var root = new RawJoint("root");
        root.Children.Add(new RawJoint("pelvis") { Transform = new JointPose(new Vector3(0f, 1f, 0f), Quaternion.Identity, Vector3.One) });
        armature.Children.Add(root);
        raw.Roots.Add(armature);
        return SkeletonBuilder.Build(raw);
    }

    /// <summary>The root walks <paramref name="distance"/> along +Z at a steady pace over <paramref name="duration"/>, bobbing <paramref name="bob"/> up at the midpoint.</summary>
    private static NativeBlobAssetReference<AnimationBlob> Walk(ref SkeletonBlob skeleton, float distance = 2f, float duration = 1f, bool rootMotion = true, float bob = 0.1f, int joint = Root)
    {
        var clip = new ClipData("walk", [new ClipChannelData(joint, ChannelPath.Translation, false, [0f, duration / 2f, duration],
            [0f, 0f, 0f, 0f, bob, distance / 2f, 0f, 0f, distance])]);
        var raw = ClipConverter.ToRaw(clip, ref skeleton);
        if (rootMotion) raw.RootMotionJoint = joint;
        return AnimationBuilder.Build(raw);
    }

    private static Vector3 Modelled(ref SkeletonBlob skeleton, ref AnimationBlob clip, float time, int joint)
    {
        using var context = SamplingContext.Create(skeleton.JointCount);
        using var pose = JointPoses.Create(skeleton.JointCount);
        var models = new Matrix4x4[skeleton.JointCount];
        context.Value.Sample(ref clip, time / clip.Duration, ref pose.Value);
        LocalToModel.Compute(ref skeleton, ref pose.Value, models);
        return models[joint].Translation;
    }

    [Test]
    public async Task a_step_travels_the_root_joints_ground_projection_and_leaves_its_height_to_the_pose()
    {
        using var skeleton = Walker();
        using var walk = Walk(ref skeleton.Value);
        using var sampler = new RootMotionSampler(skeleton);

        var travel = sampler.Delta(walk, 0.25f, 0.25f).Translation;
        var position = sampler.Position(walk, 0.5f);

        await Assert.That(travel.Z).IsEqualTo(0.5f).Within(1e-4f);
        await Assert.That(travel.X).IsEqualTo(0f).Within(1e-4f);
        await Assert.That(travel.Y).IsEqualTo(0f);
        await Assert.That(Vector3.Distance(position, new Vector3(0f, 0.1f, 1f))).IsLessThan(1e-3f);
    }

    [Test]
    public async Task travel_is_measured_in_model_space_through_the_root_joints_parents()
    {
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        using var skeleton = Walker(quarter, scale: 2f);
        using var walk = Walk(ref skeleton.Value);
        using var sampler = new RootMotionSampler(skeleton);

        var travel = sampler.Delta(walk, 0.25f, 0.25f).Translation;
        var expected = Modelled(ref skeleton.Value, ref walk.Value, 0.5f, Root) - Modelled(ref skeleton.Value, ref walk.Value, 0.25f, Root);

        await Assert.That(travel.X).IsEqualTo(expected.X).Within(Exact);
        await Assert.That(travel.Z).IsEqualTo(expected.Z).Within(Exact);
        await Assert.That(travel.Y).IsEqualTo(0f);
        // Half a metre along the root's +Z, doubled by the armature and turned onto +X.
        await Assert.That(travel.X).IsEqualTo(1f).Within(1e-4f);
        await Assert.That(travel.Z).IsEqualTo(0f).Within(1e-4f);
    }

    [Test]
    public async Task a_looping_step_over_the_end_carries_the_cycle_instead_of_jumping_back()
    {
        using var skeleton = Walker();
        using var walk = Walk(ref skeleton.Value);
        using var sampler = new RootMotionSampler(skeleton);

        var acrossEnd = sampler.Delta(walk, 0.9f, 0.2f).Translation.Z;
        var severalCycles = sampler.Delta(walk, 0f, 2.5f).Translation.Z;
        var backwardsAcrossStart = sampler.Delta(walk, 0.1f, -0.2f).Translation.Z;
        var fromWrappedTime = sampler.Delta(walk, 3.9f, 0.2f).Translation.Z;

        await Assert.That(acrossEnd).IsEqualTo(0.4f).Within(1e-4f);
        await Assert.That(severalCycles).IsEqualTo(5f).Within(1e-4f);
        await Assert.That(backwardsAcrossStart).IsEqualTo(-0.4f).Within(1e-4f);
        await Assert.That(fromWrappedTime).IsEqualTo(0.4f).Within(1e-4f);
    }

    [Test]
    public async Task a_one_shot_step_stops_at_the_clips_ends()
    {
        using var skeleton = Walker();
        using var walk = Walk(ref skeleton.Value);
        using var sampler = new RootMotionSampler(skeleton);

        await Assert.That(sampler.Delta(walk, 0.9f, 0.5f, loop: false).Translation.Z).IsEqualTo(0.2f).Within(1e-4f);
        await Assert.That(sampler.Delta(walk, 0.1f, -1f, loop: false).Translation.Z).IsEqualTo(-0.2f).Within(1e-4f);
        await Assert.That(sampler.Delta(walk, 5f, 1f, loop: false).Translation.Z).IsEqualTo(0f);
    }

    [Test]
    public async Task a_playback_at_a_rate_travels_the_clip_time_it_covered()
    {
        using var skeleton = Walker();
        using var walk = Walk(ref skeleton.Value);
        using var sampler = new RootMotionSampler(skeleton);
        using var player = new AnimationPlayer(skeleton);

        var playback = player.Play(walk, rate: 2.5f);
        player.Advance(0.1f);

        // 0.25 s of a 2 m/s clip: the player and the headless sampler cover the same clip time, so they agree to the bit.
        await Assert.That(player.RootMotion.Translation.Z).IsEqualTo(0.5f).Within(1e-4f);
        await Assert.That(player.RootMotion).IsEqualTo(sampler.Delta(walk, 0f, 0.25f));
        await Assert.That(player.GetState(playback).RootMotion).IsEqualTo(player.RootMotion);
    }

    [Test]
    public async Task a_clip_without_root_motion_travels_nothing_and_poses_as_authored()
    {
        using var skeleton = Walker();
        using var walk = Walk(ref skeleton.Value, rootMotion: false);
        using var sampler = new RootMotionSampler(skeleton);
        using var player = new AnimationPlayer(skeleton);

        player.Play(walk);
        player.Advance(0.5f);
        player.Evaluate();

        await Assert.That(walk.Value.HasRootMotion).IsFalse();
        await Assert.That(player.RootMotion).IsEqualTo(RootMotionDelta.Zero);
        await Assert.That(sampler.Delta(walk, 0f, 0.5f)).IsEqualTo(RootMotionDelta.Zero);
        await Assert.That(sampler.Position(walk, 0.5f)).IsEqualTo(Vector3.Zero);
        await Assert.That(Vector3.Distance(player.LocalPose[Root].Translation, Modelled(ref skeleton.Value, ref walk.Value, 0.5f, Root))).IsLessThan(Quantization);
        await Assert.That(player.LocalPose[Root].Translation.Z).IsEqualTo(1f).Within(1e-3f);
    }

    [Test]
    public async Task a_root_motion_clip_poses_its_root_over_the_actor_keeping_its_height_and_what_hangs_below()
    {
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        using var skeleton = Walker(quarter, scale: 2f);
        using var walk = Walk(ref skeleton.Value);
        using var player = new AnimationPlayer(skeleton);

        player.Play(walk);
        player.Advance(0.25f);
        player.Evaluate();
        var root = player.ModelMatrices[Root].Translation;
        var pelvis = player.ModelMatrices[Pelvis].Translation;
        var authoredHeight = Modelled(ref skeleton.Value, ref walk.Value, 0.25f, Root).Y;

        await Assert.That(root.X).IsEqualTo(0f).Within(Exact);
        await Assert.That(root.Z).IsEqualTo(0f).Within(Exact);
        await Assert.That(root.Y).IsEqualTo(authoredHeight).Within(Exact);
        await Assert.That(Vector3.Distance(pelvis, root + new Vector3(0f, 2f, 0f))).IsLessThan(Quantization);
    }

    [Test]
    public async Task a_joint_that_rests_off_the_origin_snaps_back_to_its_rest_on_the_ground()
    {
        var raw = new RawSkeleton();
        raw.Roots.Add(new RawJoint("hips") { Transform = new JointPose(new Vector3(0.25f, 1f, 0.5f), Quaternion.Identity, Vector3.One) });
        using var skeleton = SkeletonBuilder.Build(raw);
        var clip = ClipConverter.ToRaw(new ClipData("stride", [new ClipChannelData(0, ChannelPath.Translation, false, [0f, 1f], [0.25f, 1f, 0.5f, 0.25f, 1.25f, 3f])]), ref skeleton.Value);
        clip.RootMotionJoint = 0;
        using var stride = AnimationBuilder.Build(clip);
        using var context = SamplingContext.Create(1);
        using var pose = JointPoses.Create(1);

        context.Value.Sample(ref stride.Value, 0.5f, ref pose.Value);
        RootMotionSampler.Snap(ref skeleton.Value, ref stride.Value, ref pose.Value);

        await Assert.That(Vector3.Distance(pose.Value[0].Translation, new Vector3(0.25f, 1.125f, 0.5f))).IsLessThan(1e-3f);
    }

    [Test]
    public async Task the_actor_moved_by_the_travel_and_the_snapped_pose_add_up_to_the_authored_walk_across_a_loop()
    {
        using var skeleton = Walker();
        using var walk = Walk(ref skeleton.Value);
        using var player = new AnimationPlayer(skeleton);

        player.Play(walk);
        var actor = Vector3.Zero;
        for (var step = 0; step < 37; step++)
        {
            player.Advance(1f / 30f);
            actor += player.RootMotion.Translation;
        }

        player.Evaluate();
        var drawn = actor + player.ModelMatrices[Root].Translation;
        var time = player.GetState(player.Playbacks[0]).Time;
        var authored = Modelled(ref skeleton.Value, ref walk.Value, time, Root) + new Vector3(0f, 0f, 2f);

        await Assert.That(Vector3.Distance(drawn, authored)).IsLessThan(1e-4f);
    }

    [Test]
    public async Task in_a_blend_of_flagged_and_unflagged_clips_each_clips_travel_shows_once()
    {
        using var skeleton = Walker();
        using var flagged = Walk(ref skeleton.Value);
        using var unflagged = Walk(ref skeleton.Value, rootMotion: false);
        using var player = new AnimationPlayer(skeleton);

        player.Add(flagged, weight: 0.25f);
        player.Add(unflagged, weight: 0.75f);
        player.Advance(0.25f);
        player.Evaluate();
        var carried = player.RootMotion.Translation.Z;
        var posed = player.LocalPose[Root].Translation.Z;

        // The flagged quarter of the walk moves the actor; the unflagged three quarters stay in the pose.
        await Assert.That(carried).IsEqualTo(0.25f * 0.5f).Within(1e-4f);
        await Assert.That(posed).IsEqualTo(0.75f * 0.5f).Within(1e-4f);
        await Assert.That(carried + posed).IsEqualTo(0.5f).Within(1e-4f);
    }

    [Test]
    public async Task a_cross_fade_blends_travel_by_the_weights_that_blend_the_pose()
    {
        using var skeleton = Walker();
        using var slow = Walk(ref skeleton.Value, distance: 2f);
        using var fast = Walk(ref skeleton.Value, distance: 4f);
        using var player = new AnimationPlayer(skeleton);

        var from = player.Play(slow);
        player.Advance(0.2f);
        var to = player.Play(fast, fadeSeconds: 1f);
        player.Advance(0.25f);
        var outgoing = player.GetState(from);
        var incoming = player.GetState(to);

        await Assert.That(outgoing.Weight).IsEqualTo(0.75f).Within(Exact);
        await Assert.That(incoming.Weight).IsEqualTo(0.25f).Within(Exact);
        await Assert.That(player.RootMotion.Translation.Z)
            .IsEqualTo(outgoing.Weight * outgoing.RootMotion.Translation.Z + incoming.Weight * incoming.RootMotion.Translation.Z).Within(Exact);
        await Assert.That(player.RootMotion.Translation.Z).IsEqualTo(0.75f * 0.5f + 0.25f * 1f).Within(1e-4f);
    }

    [Test]
    public async Task layers_and_masks_scale_travel_as_they_scale_the_root_joint()
    {
        using var skeleton = Walker();
        using var slow = Walk(ref skeleton.Value, distance: 2f);
        using var fast = Walk(ref skeleton.Value, distance: 4f);
        using var lean = AdditiveAnimationBuilder.Build(ref fast.Value);
        using var player = new AnimationPlayer(skeleton);

        player.Play(slow);
        var top = player.AddLayer(AnimationLayerMode.Override, 0.4f);
        player.Play(fast, top);
        var additive = player.AddLayer(AnimationLayerMode.Additive);
        player.Play(lean, additive);
        player.Advance(0.25f);
        var layered = player.RootMotion.Translation.Z;

        player.SetMask(top, JointMask.Branch(ref skeleton.Value, "pelvis"));
        player.Advance(0.25f);
        var maskedOut = player.RootMotion.Translation.Z;

        player.SetMask(top, null);
        player.SetWeight(top, 1f);
        player.Advance(0.25f);
        var hidden = player.RootMotion.Translation.Z;

        await Assert.That(layered).IsEqualTo(0.6f * 0.5f + 0.4f * 1f).Within(1e-4f);
        await Assert.That(maskedOut).IsEqualTo(0.5f).Within(1e-4f);
        await Assert.That(hidden).IsEqualTo(1f).Within(1e-4f);
    }

    [Test]
    public async Task a_sync_group_carries_its_members_travel_across_the_loop()
    {
        using var skeleton = Walker();
        using var walk = Walk(ref skeleton.Value, distance: 2f, duration: 1f);
        using var stroll = Walk(ref skeleton.Value, distance: 2f, duration: 2f);
        using var player = new AnimationPlayer(skeleton);

        var group = player.AddSyncGroup();
        player.Synchronize(player.Add(walk), group);
        player.Synchronize(player.Add(stroll), group);
        var steps = new float[4];
        for (var i = 0; i < steps.Length; i++)
        {
            player.Advance(0.45f);
            steps[i] = player.RootMotion.Translation.Z;
        }

        // A 1.5 s mean cycle: each step covers 0.3 of both cycles, so 0.6 m each of 2 m, wrapping in the fourth step.
        foreach (var step in steps) await Assert.That(step).IsEqualTo(0.6f).Within(1e-4f);
    }

    [Test]
    public async Task a_zero_step_or_a_seek_travels_nothing()
    {
        using var skeleton = Walker();
        using var walk = Walk(ref skeleton.Value);
        using var player = new AnimationPlayer(skeleton);

        var playback = player.Play(walk);
        player.Advance(0.25f);
        player.Advance(0f);
        var still = player.RootMotion;
        var stillPlayback = player.GetState(playback).RootMotion;
        player.Seek(playback, 0.75f);
        player.Advance(0.125f);
        var afterSeek = player.RootMotion.Translation.Z;

        await Assert.That(still).IsEqualTo(RootMotionDelta.Zero);
        await Assert.That(stillPlayback).IsEqualTo(RootMotionDelta.Zero);
        await Assert.That(afterSeek).IsEqualTo(0.25f).Within(1e-4f);
    }

    [Test]
    public async Task the_same_steps_give_the_same_bits_on_every_run_and_platform()
    {
        var turn = Quaternion.Normalize(new Quaternion(0.1f, 0.7f, -0.05f, 0.7f));
        using var skeleton = Walker(turn, scale: 1.3f);
        using var walk = Walk(ref skeleton.Value, distance: 1.7f, duration: 1.1f, bob: 0.07f);
        using var sampler = new RootMotionSampler(skeleton);
        using var player = new AnimationPlayer(skeleton);

        var rate = 0.9f;
        var seconds = 1f / 60f;
        player.Play(walk, rate: rate, time: 0.3f);
        var stepped = new Vector3[40];
        for (var i = 0; i < stepped.Length; i++)
        {
            player.Advance(seconds);
            stepped[i] = player.RootMotion.Translation;
        }

        var replayed = new Vector3[stepped.Length];
        var time = 0.3f;
        var advance = seconds * rate;
        for (var i = 0; i < replayed.Length; i++)
        {
            replayed[i] = sampler.Delta(walk, time, advance).Translation;
            time += advance;
        }

        var sum = stepped.Aggregate(Vector3.Zero, (total, step) => total + step);
        await Assert.That(replayed.Select(Bits)).IsEquivalentTo(stepped.Select(Bits));
        // A snapshot of the bits, so a change of arithmetic or instruction set shows here.
        await Assert.That(Bits(sum)).IsEqualTo("0x3F96590F 0x00000000 0xBC273B71");
    }

    [Test]
    public async Task advancing_and_evaluating_root_motion_allocates_nothing()
    {
        using var skeleton = Walker();
        using var walk = Walk(ref skeleton.Value);
        using var stroll = Walk(ref skeleton.Value, duration: 2f);
        using var player = new AnimationPlayer(skeleton);
        player.Add(walk, weight: 0.5f);
        player.Add(stroll, weight: 0.5f);

        void Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                player.Advance(1f / 30f);
                player.Evaluate();
            }
        }

        Frames(100);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Frames(200);
        var after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0L);
    }

    [Test]
    public async Task a_root_motion_clip_round_trips_its_joint_through_the_archive_after_the_bytes_ozz_writes()
    {
        using var skeleton = Walker();
        using var flagged = Walk(ref skeleton.Value);
        using var unflagged = Walk(ref skeleton.Value, rootMotion: false);

        var plain = OzzArchive.WriteAnimation(ref unflagged.Value);
        var archived = OzzArchive.WriteAnimation(ref flagged.Value);
        using var read = OzzArchive.ReadAnimation(archived);
        using var readPlain = OzzArchive.ReadAnimation(plain);
        var foreign = (byte[])archived.Clone();
        foreign[plain.Length] = (byte)'q';
        var outside = (byte[])archived.Clone();
        BitConverter.GetBytes(-7).CopyTo(outside, archived.Length - 4);
        var beyond = (byte[])archived.Clone();
        BitConverter.GetBytes(3).CopyTo(beyond, archived.Length - 4);

        await Assert.That(archived.AsSpan(0, plain.Length).SequenceEqual(plain)).IsTrue();
        await Assert.That(archived.Length).IsEqualTo(plain.Length + OzzArchive.RootMotionTag.Length + 1 + 4 + 4);
        await Assert.That(read.Value.RootMotionJoint).IsEqualTo(Root);
        await Assert.That(readPlain.Value.HasRootMotion).IsFalse();
        await Assert.That(OzzArchive.WriteAnimation(ref read.Value)).IsEquivalentTo(archived, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(() => OzzArchive.ReadAnimation(foreign)).Throws<InvalidDataException>();
        await Assert.That(() => OzzArchive.ReadAnimation(outside)).Throws<InvalidDataException>();
        await Assert.That(() => OzzArchive.ReadAnimation(beyond)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task a_raw_clip_naming_a_joint_outside_its_tracks_does_not_build_and_decimation_keeps_the_joint()
    {
        using var skeleton = Walker();
        var raw = ClipConverter.ToRaw(new ClipData("walk", [new ClipChannelData(Root, ChannelPath.Translation, false, [0f, 1f], [0f, 0f, 0f, 0f, 0f, 1f])]), ref skeleton.Value);
        raw.RootMotionJoint = Root;
        var optimized = AnimationOptimizer.Optimize(raw, ref skeleton.Value, AnimationOptimizer.Setting.Default);
        raw.RootMotionJoint = 3;

        await Assert.That(optimized.RootMotionJoint).IsEqualTo(Root);
        await Assert.That(() => AnimationBuilder.Build(raw)).Throws<ArgumentException>();
    }

    private static string Bits(Vector3 v) =>
        $"0x{BitConverter.SingleToUInt32Bits(v.X):X8} 0x{BitConverter.SingleToUInt32Bits(v.Y):X8} 0x{BitConverter.SingleToUInt32Bits(v.Z):X8}";
}
