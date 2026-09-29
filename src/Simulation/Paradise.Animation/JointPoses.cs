using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

using Paradise.BLOB;

namespace Paradise.Animation;

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

/// <summary>Stores local poses in SIMD groups of four joints, one joint per Vector128 lane.</summary>
/// <remarks>The sampler writes this layout directly; unused lanes hold identity.
/// Access the blob by ref to preserve relative offsets. The per-joint indexer gathers or scatters
/// JointPose values for attachments and tests, outside the sampling hot path.</remarks>
public struct JointPoses
{
    public int JointCount;
    public BlobArray<SoaVector3> Translations;
    public BlobArray<SoaQuaternion> Rotations;
    public BlobArray<SoaVector3> Scales;

    public int GroupCount => Translations.Length;

    /// <summary>Room for <paramref name="jointCount"/> joints, every lane at identity.</summary>
    public static NativeBlobAssetReference<JointPoses> Create(int jointCount)
    {
        var builder = new StructBuilder<JointPoses>();
        Set(builder, ref builder.Value, jointCount);
        return builder.CreateNativeBlobAssetReference();
    }

    /// <summary>Sizes a pose set that is a field of a larger blob, such as the player's buffers, the same way <see cref="Create"/> sizes a standalone one.</summary>
    internal static void Set<TRoot>(StructBuilder<TRoot> builder, ref JointPoses poses, int jointCount) where TRoot : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jointCount);
        var groups = AnimationBlob.PaddedTrackCount(jointCount) / 4;
        var translations = new SoaVector3[groups];
        var rotations = new SoaQuaternion[groups];
        var scales = new SoaVector3[groups];
        for (var g = 0; g < groups; g++)
        {
            rotations[g].W = Vector128<float>.One;
            scales[g].X = scales[g].Y = scales[g].Z = Vector128<float>.One;
        }

        poses.JointCount = jointCount;
        builder.SetArray(ref poses.Translations, translations, alignment: 16);
        builder.SetArray(ref poses.Rotations, rotations, alignment: 16);
        builder.SetArray(ref poses.Scales, scales, alignment: 16);
    }

    public JointPose this[int joint]
    {
        get
        {
            Check(joint);
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
        set
        {
            Check(joint);
            var g = joint >> 2;
            var lane = joint & 3;
            ref var t = ref Translations[g];
            ref var r = ref Rotations[g];
            ref var s = ref Scales[g];
            Lane(ref t.X, lane) = value.Translation.X; Lane(ref t.Y, lane) = value.Translation.Y; Lane(ref t.Z, lane) = value.Translation.Z;
            Lane(ref r.X, lane) = value.Rotation.X; Lane(ref r.Y, lane) = value.Rotation.Y; Lane(ref r.Z, lane) = value.Rotation.Z; Lane(ref r.W, lane) = value.Rotation.W;
            Lane(ref s.X, lane) = value.Scale.X; Lane(ref s.Y, lane) = value.Scale.Y; Lane(ref s.Z, lane) = value.Scale.Z;
        }
    }

    public void CopyFrom(ReadOnlySpan<JointPose> poses)
    {
        if (poses.Length < JointCount) throw new ArgumentException($"{poses.Length} poses for {JointCount} joints.", nameof(poses));
        for (var i = 0; i < JointCount; i++) this[i] = poses[i];
    }

    public void CopyTo(Span<JointPose> poses)
    {
        if (poses.Length < JointCount) throw new ArgumentException($"{poses.Length} slots for {JointCount} joints.", nameof(poses));
        for (var i = 0; i < JointCount; i++) poses[i] = this[i];
    }

    /// <summary>For tests and tools, not the hot path.</summary>
    public JointPose[] ToArray()
    {
        var poses = new JointPose[JointCount];
        CopyTo(poses);
        return poses;
    }

    /// <summary>Copies every lane of <paramref name="source"/>; both must be sized for the same joint count.</summary>
    public void CopyFrom(ref JointPoses source)
    {
        if (source.GroupCount != GroupCount) throw new ArgumentException($"{source.JointCount} joints into {JointCount}.", nameof(source));
        source.Translations.ToSpan().CopyTo(Translations.ToSpan());
        source.Rotations.ToSpan().CopyTo(Rotations.ToSpan());
        source.Scales.ToSpan().CopyTo(Scales.ToSpan());
    }

    /// <summary>Per lane: lerp translation and scale, normalized lerp of rotations on the short arc — the same interpolation the sampler uses between keys.</summary>
    public static void Blend(ref JointPoses from, ref JointPoses to, float weight, ref JointPoses output) => Blend(ref from, ref to, weight, null, ref output);

    /// <summary>Blends like <see cref="Blend(ref JointPoses, ref JointPoses, float, ref JointPoses)"/> with the weight scaled per joint by <paramref name="mask"/>: an override layer over the pose beneath. The output may be either input.</summary>
    /// <exception cref="ArgumentException">The poses, output or mask are sized for different joint counts.</exception>
    public static void Blend(ref JointPoses from, ref JointPoses to, float weight, JointMask? mask, ref JointPoses output)
    {
        var groups = from.GroupCount;
        if (to.GroupCount != groups || output.GroupCount != groups) throw new ArgumentException("The poses and the output must be sized for the same joint count.");
        var maskGroups = JointMask.GroupsFor(mask, from.JointCount);
        var masked = !maskGroups.IsEmpty;
        var uniform = Vector128.Create(weight);
        var fromT = from.Translations.ToSpan(); var toT = to.Translations.ToSpan(); var outT = output.Translations.ToSpan();
        var fromR = from.Rotations.ToSpan(); var toR = to.Rotations.ToSpan(); var outR = output.Rotations.ToSpan();
        var fromS = from.Scales.ToSpan(); var toS = to.Scales.ToSpan(); var outS = output.Scales.ToSpan();
        for (var g = 0; g < groups; g++)
        {
            var w = masked ? uniform * maskGroups[g] : uniform;
            ref readonly var at = ref fromT[g]; ref readonly var bt = ref toT[g]; ref var ot = ref outT[g];
            ot.X = (bt.X - at.X) * w + at.X;
            ot.Y = (bt.Y - at.Y) * w + at.Y;
            ot.Z = (bt.Z - at.Z) * w + at.Z;

            ref readonly var ar = ref fromR[g]; ref readonly var br = ref toR[g];
            var flip = SoaMath.HemisphereFlip(ar, br);
            var x = ((br.X ^ flip) - ar.X) * w + ar.X;
            var y = ((br.Y ^ flip) - ar.Y) * w + ar.Y;
            var z = ((br.Z ^ flip) - ar.Z) * w + ar.Z;
            var v = ((br.W ^ flip) - ar.W) * w + ar.W;
            SoaMath.Normalize(x, y, z, v, ref outR[g]);

            ref readonly var asc = ref fromS[g]; ref readonly var bs = ref toS[g]; ref var os = ref outS[g];
            os.X = (bs.X - asc.X) * w + asc.X;
            os.Y = (bs.Y - asc.Y) * w + asc.Y;
            os.Z = (bs.Z - asc.Z) * w + asc.Z;
        }
    }

    /// <summary>ozz's additive pass: applies <paramref name="delta"/> to <paramref name="pose"/> at <paramref name="weight"/>, scaled per joint by <paramref name="mask"/> when given. The output may be either input.</summary>
    /// <remarks>Translations add the weighted offset; rotations post-multiply the delta lerped from identity, so a
    /// delta built against a reference pose reproduces its source on that pose; scales multiply by the factor lerped
    /// from one. Weight 0 leaves the pose and 1 applies the whole delta; see <see cref="Offline.AdditiveAnimationBuilder"/>.</remarks>
    /// <exception cref="ArgumentException">The poses, output or mask are sized for different joint counts.</exception>
    public static void ApplyAdditive(ref JointPoses pose, ref JointPoses delta, float weight, JointMask? mask, ref JointPoses output)
    {
        var groups = pose.GroupCount;
        if (delta.GroupCount != groups || output.GroupCount != groups) throw new ArgumentException("The pose, delta and output must be sized for the same joint count.");
        var maskGroups = JointMask.GroupsFor(mask, pose.JointCount);
        var masked = !maskGroups.IsEmpty;
        var uniform = Vector128.Create(weight);
        var one = Vector128<float>.One;
        var signBit = SoaMath.SignBit;
        var poseT = pose.Translations.ToSpan(); var deltaT = delta.Translations.ToSpan(); var outT = output.Translations.ToSpan();
        var poseR = pose.Rotations.ToSpan(); var deltaR = delta.Rotations.ToSpan(); var outR = output.Rotations.ToSpan();
        var poseS = pose.Scales.ToSpan(); var deltaS = delta.Scales.ToSpan(); var outS = output.Scales.ToSpan();
        for (var g = 0; g < groups; g++)
        {
            var w = masked ? uniform * maskGroups[g] : uniform;
            ref readonly var pt = ref poseT[g]; ref readonly var dt = ref deltaT[g]; ref var ot = ref outT[g];
            ot.X = dt.X * w + pt.X;
            ot.Y = dt.Y * w + pt.Y;
            ot.Z = dt.Z * w + pt.Z;

            // The delta on its positive-w hemisphere, so the lerp from identity takes the short arc.
            ref readonly var dr = ref deltaR[g];
            var flip = dr.W & signBit;
            var x = (dr.X ^ flip) * w;
            var y = (dr.Y ^ flip) * w;
            var z = (dr.Z ^ flip) * w;
            var v = ((dr.W ^ flip) - one) * w + one;
            var inverseLength = one / Vector128.Sqrt(x * x + y * y + z * z + v * v);
            SoaMath.Multiply(poseR[g], x * inverseLength, y * inverseLength, z * inverseLength, v * inverseLength, ref outR[g]);

            ref readonly var ps = ref poseS[g]; ref readonly var ds = ref deltaS[g]; ref var os = ref outS[g];
            os.X = ps.X * ((ds.X - one) * w + one);
            os.Y = ps.Y * ((ds.Y - one) * w + one);
            os.Z = ps.Z * ((ds.Z - one) * w + one);
        }
    }

    private void Check(int joint)
    {
        if (joint < 0 || joint >= JointCount) throw new ArgumentOutOfRangeException(nameof(joint), $"Joint {joint} of {JointCount}.");
    }

    private static ref float Lane(ref Vector128<float> vector, int lane) => ref Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vector), lane);
}
