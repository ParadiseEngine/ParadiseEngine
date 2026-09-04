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

/// <summary>
/// A skeleton's worth of local poses as a native blob in ozz's structure-of-arrays layout: joints
/// in groups of four, each component of a group one <see cref="Vector128{T}"/> with a joint per
/// lane. The sampler writes it without a transpose, and a blend or a hierarchy walk handles four
/// joints per instruction. The last group's spare lanes hold identity.
/// </summary>
/// <remarks>
/// Reach it through a <c>ref</c>, never a copy (see the BLOB README). The indexer gathers or
/// scatters one joint as a <see cref="JointPose"/> for code that thinks per joint — an attachment,
/// a test; it is not the hot path.
/// </remarks>
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

        var builder = new StructBuilder<JointPoses>();
        builder.Value.JointCount = jointCount;
        builder.SetArray(ref builder.Value.Translations, translations, alignment: 16);
        builder.SetArray(ref builder.Value.Rotations, rotations, alignment: 16);
        builder.SetArray(ref builder.Value.Scales, scales, alignment: 16);
        return builder.CreateNativeBlobAssetReference();
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
    public static void Blend(ref JointPoses from, ref JointPoses to, float weight, ref JointPoses output)
    {
        var groups = from.GroupCount;
        if (to.GroupCount != groups || output.GroupCount != groups) throw new ArgumentException("The poses and the output must be sized for the same joint count.");
        var w = Vector128.Create(weight);
        var signBit = Vector128.Create(unchecked((int)0x80000000)).AsSingle();
        var fromT = from.Translations.ToSpan(); var toT = to.Translations.ToSpan(); var outT = output.Translations.ToSpan();
        var fromR = from.Rotations.ToSpan(); var toR = to.Rotations.ToSpan(); var outR = output.Rotations.ToSpan();
        var fromS = from.Scales.ToSpan(); var toS = to.Scales.ToSpan(); var outS = output.Scales.ToSpan();
        for (var g = 0; g < groups; g++)
        {
            ref readonly var at = ref fromT[g]; ref readonly var bt = ref toT[g]; ref var ot = ref outT[g];
            ot.X = (bt.X - at.X) * w + at.X;
            ot.Y = (bt.Y - at.Y) * w + at.Y;
            ot.Z = (bt.Z - at.Z) * w + at.Z;

            ref readonly var ar = ref fromR[g]; ref readonly var br = ref toR[g]; ref var or = ref outR[g];
            var flip = (ar.X * br.X + ar.Y * br.Y + ar.Z * br.Z + ar.W * br.W) & signBit;
            var x = ((br.X ^ flip) - ar.X) * w + ar.X;
            var y = ((br.Y ^ flip) - ar.Y) * w + ar.Y;
            var z = ((br.Z ^ flip) - ar.Z) * w + ar.Z;
            var v = ((br.W ^ flip) - ar.W) * w + ar.W;
            var inverseLength = Vector128<float>.One / Vector128.Sqrt(x * x + y * y + z * z + v * v);
            or.X = x * inverseLength; or.Y = y * inverseLength; or.Z = z * inverseLength; or.W = v * inverseLength;

            ref readonly var asc = ref fromS[g]; ref readonly var bs = ref toS[g]; ref var os = ref outS[g];
            os.X = (bs.X - asc.X) * w + asc.X;
            os.Y = (bs.Y - asc.Y) * w + asc.Y;
            os.Z = (bs.Z - asc.Z) * w + asc.Z;
        }
    }

    private void Check(int joint)
    {
        if (joint < 0 || joint >= JointCount) throw new ArgumentOutOfRangeException(nameof(joint), $"Joint {joint} of {JointCount}.");
    }

    private static ref float Lane(ref Vector128<float> vector, int lane) => ref Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vector), lane);
}
