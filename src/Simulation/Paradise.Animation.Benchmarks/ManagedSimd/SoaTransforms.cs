using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Paradise.Animation.Benchmarks.ManagedSimd;

using Paradise.Animation;

/// <summary>Three components of four joints, one joint per lane.</summary>
public struct SoaVector3
{
    public Vector128<float> X, Y, Z;
}

/// <summary>Four components of four joints' rotations, one joint per lane.</summary>
public struct SoaQuaternion
{
    public Vector128<float> X, Y, Z, W;
}

/// <summary>
/// The blob runtime's structure-of-arrays pose set on plain managed arrays: joints in groups of
/// four, each component of a group one <see cref="Vector128{T}"/> with a joint per lane. This is
/// the half of the blob design that is about SIMD rather than about blobs, so the benchmark can
/// separate the two.
/// </summary>
public sealed class SoaTransforms
{
    public SoaTransforms(int jointCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jointCount);
        var groups = Managed.AnimationClip.PaddedTrackCount(jointCount) / 4;
        JointCount = jointCount;
        Translations = new SoaVector3[groups];
        Rotations = new SoaQuaternion[groups];
        Scales = new SoaVector3[groups];
        for (var g = 0; g < groups; g++)
        {
            Rotations[g].W = Vector128<float>.One;
            Scales[g].X = Scales[g].Y = Scales[g].Z = Vector128<float>.One;
        }
    }

    public int JointCount { get; }

    public SoaVector3[] Translations { get; }

    public SoaQuaternion[] Rotations { get; }

    public SoaVector3[] Scales { get; }

    public int GroupCount => Translations.Length;

    public JointPose this[int joint]
    {
        get
        {
            var g = joint >> 2;
            var lane = joint & 3;
            ref var t = ref Translations[g];
            ref var r = ref Rotations[g];
            ref var s = ref Scales[g];
            return new JointPose(
                new Vector3(Lane(ref t.X, lane), Lane(ref t.Y, lane), Lane(ref t.Z, lane)),
                new Quaternion(Lane(ref r.X, lane), Lane(ref r.Y, lane), Lane(ref r.Z, lane), Lane(ref r.W, lane)),
                new Vector3(Lane(ref s.X, lane), Lane(ref s.Y, lane), Lane(ref s.Z, lane)));
        }
    }

    public JointPose[] ToArray()
    {
        var poses = new JointPose[JointCount];
        for (var i = 0; i < JointCount; i++) poses[i] = this[i];
        return poses;
    }

    private static ref float Lane(ref Vector128<float> vector, int lane) => ref Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vector), lane);
}
