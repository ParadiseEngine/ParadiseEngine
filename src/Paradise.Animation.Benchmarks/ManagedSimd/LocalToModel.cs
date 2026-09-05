using System.Numerics;
using System.Runtime.Intrinsics;

namespace Paradise.Animation.Benchmarks.ManagedSimd;

using Paradise.Animation.Benchmarks.Managed;

/// <summary>The blob runtime's vectorized <c>LocalToModelJob</c> on managed arrays: the four local matrices of a group are built in lanes, then each multiplies its parent.</summary>
public static class LocalToModel
{
    /// <summary>Row-vector convention: <c>model[i] = local[i] × model[parent]</c>, roots multiplied by <paramref name="root"/> (identity when null).</summary>
    /// <exception cref="ArgumentException">Fewer poses or outputs than joints.</exception>
    public static void Compute(Skeleton skeleton, SoaTransforms locals, Span<Matrix4x4> models, in Matrix4x4? root = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(locals);
        var count = skeleton.JointCount;
        if (locals.JointCount < count) throw new ArgumentException($"{locals.JointCount} poses for {count} joints.", nameof(locals));
        if (models.Length < count) throw new ArgumentException($"{models.Length} outputs for {count} joints.", nameof(models));

        var rootMatrix = root ?? Matrix4x4.Identity;
        var parents = skeleton.Parents;
        var translations = locals.Translations.AsSpan();
        var rotations = locals.Rotations.AsSpan();
        var scales = locals.Scales.AsSpan();
        Span<float> rows = stackalloc float[48];
        for (var g = 0; g * 4 < count; g++)
        {
            AffineRows(in translations[g], in rotations[g], in scales[g], rows);
            var lanes = Math.Min(4, count - g * 4);
            for (var lane = 0; lane < lanes; lane++)
            {
                var joint = g * 4 + lane;
                var local = new Matrix4x4(
                    rows[lane], rows[4 + lane], rows[8 + lane], 0f,
                    rows[12 + lane], rows[16 + lane], rows[20 + lane], 0f,
                    rows[24 + lane], rows[28 + lane], rows[32 + lane], 0f,
                    rows[36 + lane], rows[40 + lane], rows[44 + lane], 1f);
                var parent = parents[joint];
                models[joint] = local * (parent == Skeleton.NoParent ? rootMatrix : models[parent]);
            }
        }
    }

    /// <summary>The twelve affine entries of four joints at once — rotation rows scaled per axis, then the translation — stored lane-major so a joint's matrix is one column of the buffer.</summary>
    private static void AffineRows(in SoaVector3 t, in SoaQuaternion q, in SoaVector3 s, Span<float> rows)
    {
        var two = Vector128.Create(2f);
        var one = Vector128<float>.One;
        var xx = q.X * q.X; var yy = q.Y * q.Y; var zz = q.Z * q.Z;
        var xy = q.X * q.Y; var wz = q.Z * q.W; var xz = q.Z * q.X; var wy = q.Y * q.W; var yz = q.Y * q.Z; var wx = q.X * q.W;
        ((one - two * (yy + zz)) * s.X).CopyTo(rows[..4]);
        (two * (xy + wz) * s.X).CopyTo(rows[4..8]);
        (two * (xz - wy) * s.X).CopyTo(rows[8..12]);
        (two * (xy - wz) * s.Y).CopyTo(rows[12..16]);
        ((one - two * (zz + xx)) * s.Y).CopyTo(rows[16..20]);
        (two * (yz + wx) * s.Y).CopyTo(rows[20..24]);
        (two * (xz + wy) * s.Z).CopyTo(rows[24..28]);
        (two * (yz - wx) * s.Z).CopyTo(rows[28..32]);
        ((one - two * (yy + xx)) * s.Z).CopyTo(rows[32..36]);
        t.X.CopyTo(rows[36..40]);
        t.Y.CopyTo(rows[40..44]);
        t.Z.CopyTo(rows[44..48]);
    }
}
