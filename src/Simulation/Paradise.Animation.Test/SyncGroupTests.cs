using Paradise.BLOB;

namespace Paradise.Animation.Test;

/// <summary>A sync group moves looping members by one normalized phase over their weight-averaged duration; joining takes the group's phase, leaving keeps the position, and a member refuses a clock of its own.</summary>
public class SyncGroupTests
{
    private const float Quantization = 2e-3f;

    /// <summary>A one-second walk and a half-second run on the chain whose hip X reads the normalized time.</summary>
    private static (NativeBlobAssetReference<SkeletonBlob> Skeleton, NativeBlobAssetReference<AnimationBlob> Walk, NativeBlobAssetReference<AnimationBlob> Run) Gaits()
    {
        var skeleton = TestRigs.Chain();
        return (skeleton, TestRigs.HipRamp(ref skeleton.Value, 1f, 1f, "walk"), TestRigs.HipRamp(ref skeleton.Value, 1f, 0.5f, "run"));
    }

    [Test]
    public async Task clips_of_different_lengths_share_one_phase_as_their_weights_change()
    {
        var (skeleton, walk, run) = Gaits();
        using var _ = skeleton;
        using var __ = walk;
        using var ___ = run;
        using var player = new AnimationPlayer(skeleton);
        var group = player.AddSyncGroup();
        var walking = player.Add(walk, 1f);
        var running = player.Add(run, 0f);
        player.Synchronize(walking, group);
        player.Synchronize(running, group);

        // All walk: the cycle lasts one second. The run follows at its own length, unsampled at zero weight.
        player.Advance(0.25f);
        var walkOnly = (player.GetState(group).Phase, player.GetState(walking).Time, player.GetState(running).Time);
        // All run: half a second per cycle.
        player.SetWeight(walking, 0f);
        player.SetWeight(running, 1f);
        player.Advance(0.125f);
        var runOnly = player.GetState(group).Phase;
        // Half each: 0.75 s per cycle.
        player.SetWeight(walking, 1f);
        player.Advance(0.1875f);
        var mixed = (player.GetState(group).Phase, player.GetState(walking).NormalizedTime, player.GetState(running).NormalizedTime);
        player.Evaluate();

        await Assert.That(walkOnly.Phase).IsEqualTo(0.25f).Within(1e-6f);
        await Assert.That(walkOnly.Item2).IsEqualTo(0.25f).Within(1e-6f);
        await Assert.That(walkOnly.Item3).IsEqualTo(0.125f).Within(1e-6f);
        await Assert.That(runOnly).IsEqualTo(0.5f).Within(1e-6f);
        await Assert.That(mixed.Phase).IsEqualTo(0.75f).Within(1e-5f);
        await Assert.That(mixed.Item2).IsEqualTo(0.75f).Within(1e-5f);
        await Assert.That(mixed.Item3).IsEqualTo(0.75f).Within(1e-5f);
        // In step, both gaits put the hip at the same place.
        await Assert.That(player.LocalPose[0].Translation.X).IsEqualTo(0.75f).Within(Quantization);
    }

    [Test]
    public async Task joining_takes_the_phase_and_leaving_keeps_the_position()
    {
        var (skeleton, walk, run) = Gaits();
        using var _ = skeleton;
        using var __ = walk;
        using var ___ = run;
        using var player = new AnimationPlayer(skeleton);
        var group = player.AddSyncGroup();
        var walking = player.Add(walk, time: 0.4f);
        var running = player.Add(run, 0f);
        player.Synchronize(walking, group);
        var adopted = player.GetState(group).Phase;
        player.Synchronize(running, group);
        var joined = player.GetState(running).Time;
        player.SetRate(running, 2f);
        player.Desynchronize(running);
        var left = player.GetState(running).Time;
        player.Advance(0.1f);
        var afterLeaving = (player.GetState(running).Time, player.GetState(walking).Time);
        player.Remove(group);
        player.Advance(0.1f);

        // An empty group takes its first member's position; a later member jumps to the group's.
        await Assert.That(adopted).IsEqualTo(0.4f).Within(1e-6f);
        await Assert.That(joined).IsEqualTo(0.2f).Within(1e-6f);
        await Assert.That(left).IsEqualTo(0.2f).Within(1e-6f);
        // Its own rate, set while the group drove it, applies once it leaves.
        await Assert.That(afterLeaving.Item1).IsEqualTo(0.4f).Within(1e-5f);
        await Assert.That(afterLeaving.Item2).IsEqualTo(0.5f).Within(1e-5f);
        await Assert.That(player.GetState(walking).Time).IsEqualTo(0.6f).Within(1e-5f);
        await Assert.That(player.GetState(walking).SyncGroup).IsEqualTo(default(SyncGroupHandle));
    }

    [Test]
    public async Task no_weight_holds_the_phase_and_a_negative_rate_runs_it_backwards()
    {
        var (skeleton, walk, run) = Gaits();
        using var _ = skeleton;
        using var __ = walk;
        using var ___ = run;
        using var player = new AnimationPlayer(skeleton);
        var group = player.AddSyncGroup();
        var walking = player.Add(walk, 0f, time: 0.1f);
        player.Synchronize(walking, group);
        player.Advance(1f);
        var held = player.GetState(group).Phase;
        player.SetWeight(walking, 1f);
        player.SetRate(group, -1f);
        player.Advance(0.25f);
        var backwards = (player.GetState(group).Phase, player.GetState(walking).Time);
        player.SetPhase(group, 1.25f);
        var moved = (player.GetState(group).Phase, player.GetState(walking).Time);

        await Assert.That(held).IsEqualTo(0.1f).Within(1e-6f);
        await Assert.That(backwards.Phase).IsEqualTo(0.85f).Within(1e-5f);
        await Assert.That(backwards.Time).IsEqualTo(0.85f).Within(1e-5f);
        await Assert.That(moved.Phase).IsEqualTo(0.25f).Within(1e-6f);
        await Assert.That(moved.Time).IsEqualTo(0.25f).Within(1e-6f);
    }

    [Test]
    public async Task only_looping_running_playbacks_join_and_members_refuse_a_clock_of_their_own()
    {
        var (skeleton, walk, run) = Gaits();
        using var _ = skeleton;
        using var __ = walk;
        using var ___ = run;
        using var player = new AnimationPlayer(skeleton);
        var group = player.AddSyncGroup();
        var once = player.Add(walk, loop: false);
        var paused = player.Add(walk);
        player.Pause(paused);
        var member = player.Add(run);
        player.Synchronize(member, group);

        await Assert.That(() => player.Synchronize(once, group)).Throws<InvalidOperationException>();
        await Assert.That(() => player.Synchronize(paused, group)).Throws<InvalidOperationException>();
        await Assert.That(() => player.Seek(member, 0.1f)).Throws<InvalidOperationException>();
        await Assert.That(() => player.Pause(member)).Throws<InvalidOperationException>();
        await Assert.That(player.GetState(group).MemberCount).IsEqualTo(1);
        player.Remove(member);
        await Assert.That(player.GetState(group).MemberCount).IsEqualTo(0);
        player.Remove(group);
        await Assert.That(player.Contains(group)).IsFalse();
        await Assert.That(() => player.SetPhase(group, 0f)).Throws<ArgumentException>();
    }
}
