using System.Runtime.Intrinsics;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>Blends any number of weighted local poses into one, normalizing every joint by the weight it received.</summary>
/// <remarks>Weights are relative: multiplying every weight by one positive factor gives the same pose. Translations and
/// scales average by weight; rotations accumulate on the running sum's hemisphere and normalize once in
/// <see cref="Resolve"/>, so N inputs cost N accumulations and one normalization rather than N−1 pairwise blends, whose
/// result depends on their order. Inputs are summed relative to the largest weight seen, which keeps any finite weights
/// from overflowing. A joint whose total weight is below <see cref="MinimumJointWeight"/> of the largest input blends
/// toward the fallback pose by the shortfall, reaching it exactly at zero; only masked inputs leave a joint that light.
/// Lives in a native blob: access it by ref. Nothing allocates after creation.</remarks>
public struct PoseBlender
{
    /// <summary>The share of the largest input weight below which a joint's total gives way to the fallback pose.</summary>
    public const float MinimumJointWeight = 1e-4f;

    private int _jointCount;
    private int _count;
    private float _largest;
    private BlobArray<SoaVector3> _translations;
    private BlobArray<SoaQuaternion> _rotations;
    private BlobArray<SoaVector3> _scales;
    private BlobArray<Vector128<float>> _weights;

    public readonly int JointCount => _jointCount;

    /// <summary>Inputs added since <see cref="Reset"/> with a positive weight.</summary>
    public readonly int Count => _count;

    public static NativeBlobAssetReference<PoseBlender> Create(int jointCount)
    {
        var builder = new StructBuilder<PoseBlender>();
        Set(builder, ref builder.Value, jointCount);
        return builder.CreateNativeBlobAssetReference();
    }

    /// <summary>Sizes a blender that is a field of a larger blob, the same way <see cref="Create"/> sizes a standalone one.</summary>
    internal static void Set<TRoot>(StructBuilder<TRoot> builder, ref PoseBlender blender, int jointCount) where TRoot : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jointCount);
        var groups = AnimationBlob.PaddedTrackCount(jointCount) / 4;
        blender._jointCount = jointCount;
        builder.SetArray(ref blender._translations, new SoaVector3[groups], alignment: 16);
        builder.SetArray(ref blender._rotations, new SoaQuaternion[groups], alignment: 16);
        builder.SetArray(ref blender._scales, new SoaVector3[groups], alignment: 16);
        builder.SetArray(ref blender._weights, new Vector128<float>[groups], alignment: 16);
    }

    /// <summary>Forgets every input; the next <see cref="Add"/> starts a new blend.</summary>
    public void Reset()
    {
        _count = 0;
        _largest = 0f;
    }

    /// <summary>Adds <paramref name="pose"/> at <paramref name="weight"/>, scaled per joint by <paramref name="mask"/> when given; a zero weight adds nothing.</summary>
    /// <remarks>Poses record no skeleton, so a mask is checked only for its joint count; it must be made for the pose's
    /// skeleton, which <see cref="AnimationPlayer"/> checks for its layers.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">A negative or non-finite weight.</exception>
    /// <exception cref="ArgumentException">The pose or mask is sized for another joint count.</exception>
    public void Add(ref JointPoses pose, float weight, JointMask? mask = null)
    {
        if (!float.IsFinite(weight) || weight < 0f) throw new ArgumentOutOfRangeException(nameof(weight), weight, "A blend weight is finite and non-negative.");
        if (pose.JointCount != _jointCount) throw new ArgumentException($"A pose of {pose.JointCount} joints for a blender of {_jointCount}.", nameof(pose));
        var maskGroups = JointMask.GroupsFor(mask, _jointCount);
        if (weight == 0f) return;

        float scaled;
        var first = _count == 0;
        if (first)
        {
            _largest = weight;
            scaled = 1f;
        }
        else if (weight > _largest)
        {
            // The sums stay relative to the largest weight, so a heavier input rescales what came before.
            Rescale(_largest / weight);
            _largest = weight;
            scaled = 1f;
        }
        else
        {
            scaled = weight / _largest;
        }

        var uniform = Vector128.Create(scaled);
        var poseT = pose.Translations.ToSpan(); var poseR = pose.Rotations.ToSpan(); var poseS = pose.Scales.ToSpan();
        var sumT = _translations.ToSpan(); var sumR = _rotations.ToSpan(); var sumS = _scales.ToSpan();
        var weights = _weights.ToSpan();
        var masked = !maskGroups.IsEmpty;
        if (first)
        {
            for (var g = 0; g < weights.Length; g++)
            {
                var w = masked ? uniform * maskGroups[g] : uniform;
                ref readonly var t = ref poseT[g]; ref var st = ref sumT[g];
                st.X = t.X * w; st.Y = t.Y * w; st.Z = t.Z * w;
                ref readonly var r = ref poseR[g]; ref var sr = ref sumR[g];
                sr.X = r.X * w; sr.Y = r.Y * w; sr.Z = r.Z * w; sr.W = r.W * w;
                ref readonly var s = ref poseS[g]; ref var ss = ref sumS[g];
                ss.X = s.X * w; ss.Y = s.Y * w; ss.Z = s.Z * w;
                weights[g] = w;
            }
        }
        else
        {
            for (var g = 0; g < weights.Length; g++)
            {
                var w = masked ? uniform * maskGroups[g] : uniform;
                ref readonly var t = ref poseT[g]; ref var st = ref sumT[g];
                st.X += t.X * w; st.Y += t.Y * w; st.Z += t.Z * w;
                ref readonly var r = ref poseR[g]; ref var sr = ref sumR[g];
                var flip = SoaMath.HemisphereFlip(sr, r);
                sr.X += (r.X ^ flip) * w; sr.Y += (r.Y ^ flip) * w; sr.Z += (r.Z ^ flip) * w; sr.W += (r.W ^ flip) * w;
                ref readonly var s = ref poseS[g]; ref var ss = ref sumS[g];
                ss.X += s.X * w; ss.Y += s.Y * w; ss.Z += s.Z * w;
                weights[g] += w;
            }
        }

        _count++;
    }

    /// <summary>Writes the normalized blend to <paramref name="output"/>; joints no input reached take <paramref name="fallback"/>, as does every joint when nothing was added.</summary>
    /// <remarks>The output may be the fallback. The rest pose is the usual fallback for absolute poses and the identity pose for additive deltas.</remarks>
    /// <exception cref="ArgumentException">The fallback or output is sized for another joint count.</exception>
    public void Resolve(ref JointPoses fallback, ref JointPoses output)
    {
        if (fallback.JointCount != _jointCount) throw new ArgumentException($"A fallback of {fallback.JointCount} joints for a blender of {_jointCount}.", nameof(fallback));
        if (output.JointCount != _jointCount) throw new ArgumentException($"An output of {output.JointCount} joints for a blender of {_jointCount}.", nameof(output));
        if (_count == 0)
        {
            output.CopyFrom(ref fallback);
            return;
        }

        var minimum = Vector128.Create(MinimumJointWeight);
        var fallbackT = fallback.Translations.ToSpan(); var fallbackR = fallback.Rotations.ToSpan(); var fallbackS = fallback.Scales.ToSpan();
        var outT = output.Translations.ToSpan(); var outR = output.Rotations.ToSpan(); var outS = output.Scales.ToSpan();
        var sumT = _translations.ToSpan(); var sumR = _rotations.ToSpan(); var sumS = _scales.ToSpan();
        var weights = _weights.ToSpan();
        for (var g = 0; g < weights.Length; g++)
        {
            var total = weights[g];
            var shortfall = Vector128.Max(minimum - total, Vector128<float>.Zero);
            var inverse = Vector128<float>.One / (total + shortfall);

            ref readonly var st = ref sumT[g]; ref readonly var ft = ref fallbackT[g];
            var tx = (st.X + ft.X * shortfall) * inverse;
            var ty = (st.Y + ft.Y * shortfall) * inverse;
            var tz = (st.Z + ft.Z * shortfall) * inverse;

            ref readonly var sr = ref sumR[g]; ref readonly var fr = ref fallbackR[g];
            var flip = SoaMath.HemisphereFlip(sr, fr);
            var rx = sr.X + (fr.X ^ flip) * shortfall;
            var ry = sr.Y + (fr.Y ^ flip) * shortfall;
            var rz = sr.Z + (fr.Z ^ flip) * shortfall;
            var rw = sr.W + (fr.W ^ flip) * shortfall;

            ref readonly var ss = ref sumS[g]; ref readonly var fs = ref fallbackS[g];
            var sx = (ss.X + fs.X * shortfall) * inverse;
            var sy = (ss.Y + fs.Y * shortfall) * inverse;
            var sz = (ss.Z + fs.Z * shortfall) * inverse;

            ref var ot = ref outT[g];
            ot.X = tx; ot.Y = ty; ot.Z = tz;
            SoaMath.Normalize(rx, ry, rz, rw, ref outR[g]);
            ref var os = ref outS[g];
            os.X = sx; os.Y = sy; os.Z = sz;
        }
    }

    private void Rescale(float factor)
    {
        var f = Vector128.Create(factor);
        var sumT = _translations.ToSpan(); var sumR = _rotations.ToSpan(); var sumS = _scales.ToSpan();
        var weights = _weights.ToSpan();
        for (var g = 0; g < weights.Length; g++)
        {
            ref var st = ref sumT[g];
            st.X *= f; st.Y *= f; st.Z *= f;
            ref var sr = ref sumR[g];
            sr.X *= f; sr.Y *= f; sr.Z *= f; sr.W *= f;
            ref var ss = ref sumS[g];
            ss.X *= f; ss.Y *= f; ss.Z *= f;
            weights[g] *= f;
        }
    }
}
