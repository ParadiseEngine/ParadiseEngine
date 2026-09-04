using System.Numerics;

using Paradise.Animation.Offline;

namespace Paradise.Animation.Test;

/// <summary>The clip converter fills rest pose and bakes STEP, the optimizer drops what a lerp reproduces, and both refuse what the skeleton cannot take.</summary>
public class OfflineTests
{
    [Test]
    public async Task the_converter_fills_rest_pose_and_bakes_step_holds()
    {
        using var skeleton = TestRigs.Chain();
        var clip = new ClipData("hop", [new ClipChannelData(0, ChannelPath.Translation, true, [0f, 0.5f, 1f], [0, 1, 0, 0, 2, 0, 0, 3, 0])]);

        var raw = ClipConverter.ToRaw(clip, ref skeleton.Value);

        await Assert.That(raw.Duration).IsEqualTo(1f);
        await Assert.That(raw.Tracks.Count).IsEqualTo(3);
        await Assert.That(raw.Tracks[0].Translations.Select(k => k.Time).ToArray()).IsEquivalentTo(new[] { 0f, 0.5f - ClipConverter.StepLead, 0.5f, 1f - ClipConverter.StepLead, 1f });
        await Assert.That(raw.Tracks[0].Translations[1].Value).IsEqualTo(new Vector3(0, 1, 0));
        await Assert.That(raw.Tracks[1].Rotations.Single()).IsEqualTo(new RotationKey(0f, TestRigs.QuarterTurnZ));
        await Assert.That(raw.Tracks[2].Translations.Single().Value).IsEqualTo(Vector3.Zero);
        await Assert.That(raw.IsValid).IsTrue();
    }

    [Test]
    public async Task a_pose_clip_gets_the_minimum_duration_and_a_stray_joint_is_refused()
    {
        using var skeleton = TestRigs.Chain();
        var pose = new ClipData("pose", [new ClipChannelData(1, ChannelPath.Rotation, false, [0f], [0, 0, 0, 1])]);
        var stray = new ClipData("stray", [new ClipChannelData(9, ChannelPath.Rotation, false, [0f], [0, 0, 0, 1])]);

        await Assert.That(ClipConverter.ToRaw(pose, ref skeleton.Value).Duration).IsEqualTo(ClipConverter.MinimumDuration);
        var error = await Assert.That(() => ClipConverter.ToRaw(stray, ref skeleton.Value)).Throws<ArgumentException>();
        await Assert.That(error!.Message).Contains("joint 9");
    }

    [Test]
    public async Task the_optimizer_drops_keys_a_lerp_reproduces_and_keeps_the_rest()
    {
        using var skeleton = TestRigs.Chain();
        var raw = new RawAnimation { Name = "walk", Duration = 1f };
        for (var i = 0; i < skeleton.Value.JointCount; i++) raw.Tracks.Add(new RawTrack());
        for (var k = 0; k <= 10; k++)
        {
            var t = k / 10f;
            raw.Tracks[0].Translations.Add(new TranslationKey(t, new Vector3(t, 0, 0)));
            raw.Tracks[0].Rotations.Add(new RotationKey(t, Quaternion.Identity));
            raw.Tracks[1].Rotations.Add(new RotationKey(t, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.05f * t * t)));
        }

        var optimized = AnimationOptimizer.Optimize(raw, ref skeleton.Value, AnimationOptimizer.Setting.Default);
        var strict = AnimationOptimizer.Optimize(raw, ref skeleton.Value, AnimationOptimizer.Setting.Default, new Dictionary<int, AnimationOptimizer.Setting> { [1] = new(1e-6f, 1f) });

        await Assert.That(optimized.Tracks[0].Translations.Count).IsEqualTo(2);
        await Assert.That(optimized.Tracks[0].Rotations).IsEmpty();
        await Assert.That(optimized.Tracks[1].Rotations.Count).IsGreaterThan(2);
        await Assert.That(optimized.Tracks[1].Rotations.Count).IsLessThan(11);
        // Not all 11: below ~1e-3 radians the dot product of two rotations rounds to 1 in float, and
        // ozz's angular distance is zero there too.
        await Assert.That(strict.Tracks[1].Rotations.Count).IsGreaterThan(optimized.Tracks[1].Rotations.Count);
        await Assert.That(optimized.IsValid).IsTrue();
    }

    [Test]
    public async Task the_optimizer_refuses_a_clip_that_does_not_fit_the_skeleton()
    {
        var raw = new RawAnimation { Duration = 1f };
        using var skeleton = TestRigs.Chain();

        await Assert.That(() => AnimationOptimizer.Optimize(raw, ref skeleton.Value, AnimationOptimizer.Setting.Default)).Throws<ArgumentException>();
        await Assert.That(() => AnimationBuilder.Build(new RawAnimation { Duration = 0f })).Throws<ArgumentException>();
    }

    [Test]
    public async Task a_track_with_keys_far_apart_in_the_stream_is_split_so_the_back_link_fits()
    {
        // Keys sort by the time of the key before them, so between track 0's keys at 0.5 and 1
        // sit every other track's keys whose predecessor lies in that half: 1023 × 70 of them,
        // past the 16-bit back-link — the builder must insert a midpoint key on track 0.
        var raw = new RawAnimation { Name = "wide", Duration = 1f };
        for (var i = 0; i < SkeletonBlob.MaxJoints; i++)
        {
            var track = new RawTrack();
            if (i == 0)
            {
                track.Translations.Add(new TranslationKey(0f, Vector3.Zero));
                track.Translations.Add(new TranslationKey(0.5f, new Vector3(1, 0, 0)));
                track.Translations.Add(new TranslationKey(1f, new Vector3(3, 0, 0)));
            }
            else
            {
                for (var k = 0; k < 140; k++) track.Translations.Add(new TranslationKey(k / 139f, new Vector3(k, 0, 0)));
            }

            raw.Tracks.Add(track);
        }

        using var built = AnimationBuilder.Build(raw);
        using var read = OzzArchive.ReadAnimation(OzzArchive.WriteAnimation(ref built.Value));
        using var context = SamplingContext.Create(read.Value.TrackCount);
        using var poses = JointPoses.Create(read.Value.TrackCount);
        context.Value.Sample(ref read.Value, 0.75f, ref poses.Value);

        await Assert.That(read.Value.Translations.KeyCount).IsGreaterThan(1023 * 140 + 3);
        await Assert.That(MathF.Abs(poses.Value[0].Translation.X - 2f)).IsLessThan(0.01f);
        await Assert.That(MathF.Abs(poses.Value[5].Translation.X - 104.25f)).IsLessThan(0.1f);
    }
}
