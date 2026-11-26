using System.Numerics;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>How far a clip's root-motion joint travelled over a step, in model space: the actor's space, so the host turns it into world space by the actor's rotation.</summary>
/// <param name="Translation">The planar travel; Y is always zero, since height stays in the pose.</param>
public readonly record struct RootMotionDelta(Vector3 Translation)
{
    public static RootMotionDelta Zero => default;

    public static RootMotionDelta operator +(RootMotionDelta left, RootMotionDelta right) => new(left.Translation + right.Translation);
}

/// <summary>Samples a clip's root motion without a player or renderer: where its <see cref="AnimationBlob.RootMotionJoint"/> travels between two clip times, and the pose with that travel removed.</summary>
/// <remarks>
/// <para>Travel is the root-motion joint's model-space position projected on the ground plane (X and Z; the data contract
/// is Y-up), measured on the pose as sampled, before <see cref="Snap"/>. A looping clip's travel across its end adds the
/// whole cycle's travel once per wrap, so a step over the boundary never moves the body back by the cycle.</para>
/// <para><see cref="Snap"/> is the other half of the contract: it moves the joint back over where it stands in the rest
/// pose on the ground plane, keeping its height, rotation and everything below it, so a host that moves the actor by the
/// travel draws the body moved once. A clip without root motion has no travel and is never snapped.</para>
/// <para>Results depend only on the clip, the skeleton and the times, through scalar IEEE arithmetic, so the same inputs
/// give the same bits on every run and platform. Sampling allocates nothing. One owner uses a sampler at a time.</para>
/// </remarks>
public sealed class RootMotionSampler : IDisposable
{
    private readonly NativeBlobAssetReference<SkeletonBlob> _skeleton;
    private readonly NativeBlobAssetReference<SamplingContext> _context;
    private readonly NativeBlobAssetReference<JointPoses> _pose;
    private bool _disposed;

    /// <param name="skeleton">The rig the clips are sampled on; the sampler does not own it.</param>
    public RootMotionSampler(NativeBlobAssetReference<SkeletonBlob> skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        _skeleton = skeleton;
        var joints = skeleton.Value.JointCount;
        _context = SamplingContext.Create(joints);
        _pose = JointPoses.Create(joints);
    }

    public NativeBlobAssetReference<SkeletonBlob> Skeleton => _skeleton;

    /// <summary>The clip's root-motion joint in model space at <paramref name="time"/> seconds, clamped to the clip, before any snap; zero for a clip without root motion.</summary>
    /// <exception cref="ArgumentException">The clip's track count differs from the skeleton's joint count.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A non-finite time.</exception>
    public Vector3 Position(NativeBlobAssetReference<AnimationBlob> clip, float time)
    {
        ref var animation = ref Check(clip, nameof(clip));
        CheckFinite(time, nameof(time));
        if (!animation.HasRootMotion) return Vector3.Zero;
        return Sample(ref _skeleton.Value, ref animation, ref _context.Value, ref _pose.Value, Math.Clamp(time, 0f, animation.Duration));
    }

    /// <summary>The travel of a clip played from <paramref name="time"/> seconds for <paramref name="advance"/> seconds of clip time.</summary>
    /// <param name="time">Where the step starts: wrapped into the clip when it loops, clamped when it does not.</param>
    /// <param name="advance">Clip seconds the step covers, the elapsed time times the playback rate: negative plays backwards, and a rate-matched step travels as far as the clip time it covers.</param>
    /// <param name="loop">Whether time wraps at the ends, carrying the cycle's travel across, or clamps.</param>
    /// <returns>Zero for a clip without root motion.</returns>
    /// <exception cref="ArgumentException">The clip's track count differs from the skeleton's joint count.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A non-finite time or advance.</exception>
    public RootMotionDelta Delta(NativeBlobAssetReference<AnimationBlob> clip, float time, float advance, bool loop = true)
    {
        ref var animation = ref Check(clip, nameof(clip));
        CheckFinite(time, nameof(time));
        CheckFinite(advance, nameof(advance));
        if (!animation.HasRootMotion) return RootMotionDelta.Zero;

        var from = ClipTime.Place(time, animation.Duration, loop);
        var to = ClipTime.Place(from + advance, animation.Duration, loop, out var cycles);
        var track = default(RootTrack);
        return Travel(ref _skeleton.Value, ref animation, ref _context.Value, ref _pose.Value, ref track, from, to, cycles);
    }

    /// <summary>Removes a root-motion clip's travel from a pose sampled from it: its root-motion joint moves back over its rest position on the ground plane, keeping its height, rotation and the joints below it.</summary>
    /// <remarks>A pose of a clip without root motion is left as it is. <see cref="AnimationPlayer"/> does this to each root-motion playback before it blends.</remarks>
    /// <exception cref="ArgumentException">The clip names a joint the skeleton or the pose lacks.</exception>
    public static void Snap(ref SkeletonBlob skeleton, ref AnimationBlob clip, ref JointPoses pose)
    {
        if (!clip.HasRootMotion) return;
        var joint = clip.RootMotionJoint;
        if (joint >= skeleton.JointCount || joint >= pose.JointCount) throw new ArgumentException($"The clip's root motion names joint {joint}; the skeleton has {skeleton.JointCount} and the pose {pose.JointCount}.", nameof(clip));
        SnapJoint(ref skeleton, ref pose, joint);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Dispose();
        _pose.Dispose();
    }

    /// <summary>The planar travel from <paramref name="from"/> to <paramref name="to"/> seconds across <paramref name="cycles"/> wraps of a looping clip, reusing and updating <paramref name="track"/>'s last position.</summary>
    internal static RootMotionDelta Travel(ref SkeletonBlob skeleton, ref AnimationBlob clip, ref SamplingContext context, ref JointPoses scratch, ref RootTrack track, float from, float to, float cycles)
    {
        var start = track.Known && track.Time == from ? track.Position : Sample(ref skeleton, ref clip, ref context, ref scratch, from);
        var end = from == to ? start : Sample(ref skeleton, ref clip, ref context, ref scratch, to);
        track.Known = true;
        track.Time = to;
        track.Position = end;

        var travel = end - start;
        if (cycles != 0f)
        {
            if (!track.HasCycle)
            {
                var first = Sample(ref skeleton, ref clip, ref context, ref scratch, 0f);
                var last = Sample(ref skeleton, ref clip, ref context, ref scratch, clip.Duration);
                track.Cycle = last - first;
                track.HasCycle = true;
            }

            travel += track.Cycle * cycles;
        }

        return new RootMotionDelta(new Vector3(travel.X, 0f, travel.Z));
    }

    /// <summary>Moves <paramref name="joint"/>'s local translation so it stands over its rest position on the ground plane, at the height it has.</summary>
    internal static void SnapJoint(ref SkeletonBlob skeleton, ref JointPoses pose, int joint)
    {
        var posed = ModelPosition(ref skeleton, ref pose, joint);
        var rest = RestPosition(ref skeleton, joint);
        var offset = new Vector3(rest.X - posed.X, 0f, rest.Z - posed.Z);
        if (offset == Vector3.Zero) return;

        var parents = skeleton.Parents.ToSpan();
        var depth = 0;
        for (var ancestor = parents[joint]; ancestor != SkeletonBlob.NoParent; ancestor = parents[ancestor]) depth++;
        Span<short> chain = stackalloc short[depth];
        var at = 0;
        for (var ancestor = parents[joint]; ancestor != SkeletonBlob.NoParent; ancestor = parents[ancestor]) chain[at++] = ancestor;

        // Into the parent's frame: undo each ancestor's rotation and scale from the top of the chain down.
        for (var i = depth - 1; i >= 0; i--) offset = InverseLinear(pose[chain[i]], offset);
        var local = pose[joint];
        pose[joint] = new JointPose(local.Translation + offset, local.Rotation, local.Scale);
    }

    private static Vector3 Sample(ref SkeletonBlob skeleton, ref AnimationBlob clip, ref SamplingContext context, ref JointPoses scratch, float time)
    {
        context.Sample(ref clip, time / clip.Duration, ref scratch);
        return ModelPosition(ref skeleton, ref scratch, clip.RootMotionJoint);
    }

    private static Vector3 ModelPosition(ref SkeletonBlob skeleton, ref JointPoses pose, int joint)
    {
        var parents = skeleton.Parents.ToSpan();
        var position = pose[joint].Translation;
        for (var ancestor = parents[joint]; ancestor != SkeletonBlob.NoParent; ancestor = parents[ancestor]) position = Transform(pose[ancestor], position);
        return position;
    }

    private static Vector3 RestPosition(ref SkeletonBlob skeleton, int joint)
    {
        var parents = skeleton.Parents.ToSpan();
        var rest = skeleton.RestPoses.ToSpan();
        var position = rest[joint].Translation;
        for (var ancestor = parents[joint]; ancestor != SkeletonBlob.NoParent; ancestor = parents[ancestor]) position = Transform(rest[ancestor], position);
        return position;
    }

    // Scalar arithmetic in a fixed order: vector dot products and fused multiply-adds round differently across instruction sets, and the travel must not.

    /// <summary>A point through a local transform, row-vector convention: scale, then rotate, then translate.</summary>
    private static Vector3 Transform(in JointPose local, Vector3 point)
    {
        Rows(local.Rotation, out var r0, out var r1, out var r2);
        var x = point.X * local.Scale.X;
        var y = point.Y * local.Scale.Y;
        var z = point.Z * local.Scale.Z;
        return new Vector3(
            x * r0.X + y * r1.X + z * r2.X + local.Translation.X,
            x * r0.Y + y * r1.Y + z * r2.Y + local.Translation.Y,
            x * r0.Z + y * r1.Z + z * r2.Z + local.Translation.Z);
    }

    /// <summary>A direction back through a local transform's rotation and scale; a collapsed scale axis takes no part of it.</summary>
    private static Vector3 InverseLinear(in JointPose local, Vector3 direction)
    {
        Rows(local.Rotation, out var r0, out var r1, out var r2);
        return new Vector3(
            Divide(direction.X * r0.X + direction.Y * r0.Y + direction.Z * r0.Z, local.Scale.X),
            Divide(direction.X * r1.X + direction.Y * r1.Y + direction.Z * r1.Z, local.Scale.Y),
            Divide(direction.X * r2.X + direction.Y * r2.Y + direction.Z * r2.Z, local.Scale.Z));
    }

    private static float Divide(float value, float scale) => scale == 0f ? 0f : value / scale;

    /// <summary>The rotation matrix's rows, as <see cref="LocalToModel.Affine"/> writes them.</summary>
    private static void Rows(Quaternion q, out Vector3 r0, out Vector3 r1, out Vector3 r2)
    {
        var xx = q.X * q.X; var yy = q.Y * q.Y; var zz = q.Z * q.Z;
        var xy = q.X * q.Y; var wz = q.Z * q.W; var xz = q.Z * q.X; var wy = q.Y * q.W; var yz = q.Y * q.Z; var wx = q.X * q.W;
        r0 = new Vector3(1f - 2f * (yy + zz), 2f * (xy + wz), 2f * (xz - wy));
        r1 = new Vector3(2f * (xy - wz), 1f - 2f * (zz + xx), 2f * (yz + wx));
        r2 = new Vector3(2f * (xz + wy), 2f * (yz - wx), 1f - 2f * (yy + xx));
    }

    private ref AnimationBlob Check(NativeBlobAssetReference<AnimationBlob> clip, string parameter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(clip, parameter);
        ref var animation = ref clip.Value;
        if (animation.TrackCount != _skeleton.Value.JointCount)
        {
            throw new ArgumentException($"The clip '{animation.Name.ToString()}' has {animation.TrackCount} tracks; the skeleton has {_skeleton.Value.JointCount} joints.", parameter);
        }

        return ref animation;
    }

    private static void CheckFinite(float value, string parameter)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(parameter, value, "The value is not finite.");
    }
}

/// <summary>A playback's last sampled root-motion position, so consecutive steps sample each clip time once, and its clip's travel over one cycle.</summary>
/// <remarks><c>default</c> knows nothing; reset it whenever the clip changes.</remarks>
internal struct RootTrack
{
    public bool Known;
    public float Time;
    public Vector3 Position;
    public bool HasCycle;
    public Vector3 Cycle;
}

/// <summary>Where a clock lands in a clip: wrapped for a looping clip, clamped for a one-shot.</summary>
internal static class ClipTime
{
    public static float Place(float time, float duration, bool loop) => Place(time, duration, loop, out _);

    /// <param name="cycles">How many times a looping clock wrapped to land there: negative backwards, zero for a one-shot.</param>
    public static float Place(float time, float duration, bool loop, out float cycles)
    {
        cycles = 0f;
        if (!(duration > 0f)) return 0f;
        if (!loop) return Math.Clamp(time, 0f, duration);
        var turns = MathF.Floor(time / duration);
        var wrapped = time - turns * duration;
        if (wrapped >= 0f && wrapped < duration)
        {
            cycles = turns;
            return wrapped;
        }

        // Rounding put a time on a cycle boundary just outside the cycle the division chose: it is that boundary.
        if (wrapped < 0f) cycles = turns;
        else if (wrapped >= duration) cycles = turns + 1f;

        // An overflowed step wraps to NaN, which fails every comparison and falls back to the start.
        return 0f;
    }
}
