using System.Numerics;

namespace Paradise.Animation;

/// <summary>Turns local joint poses into model-space matrices by walking the skeleton's parents; ozz's <c>LocalToModelJob</c>. Allocates nothing.</summary>
public static class LocalToModel
{
    /// <summary>Row-vector convention: <c>model[i] = local[i] × model[parent]</c>, roots multiplied by <paramref name="root"/> (identity when null).</summary>
    /// <exception cref="ArgumentException">Fewer poses or outputs than joints.</exception>
    public static void Compute(ref SkeletonBlob skeleton, ReadOnlySpan<JointPose> locals, Span<Matrix4x4> models, in Matrix4x4? root = null)
    {
        var count = skeleton.JointCount;
        if (locals.Length < count) throw new ArgumentException($"{locals.Length} poses for {count} joints.", nameof(locals));
        if (models.Length < count) throw new ArgumentException($"{models.Length} outputs for {count} joints.", nameof(models));

        var rootMatrix = root ?? Matrix4x4.Identity;
        var parents = skeleton.Parents.ToSpan();
        for (var i = 0; i < count; i++)
        {
            var parent = parents[i];
            models[i] = Affine(in locals[i]) * (parent == SkeletonBlob.NoParent ? rootMatrix : models[parent]);
        }
    }

    /// <summary>Scale, then rotate, then translate, written straight into the rows: the rotation matrix's rows scaled per axis and the translation as the fourth row, instead of three matrices multiplied.</summary>
    public static Matrix4x4 Affine(in JointPose pose)
    {
        var q = pose.Rotation;
        var xx = q.X * q.X; var yy = q.Y * q.Y; var zz = q.Z * q.Z;
        var xy = q.X * q.Y; var wz = q.Z * q.W; var xz = q.Z * q.X; var wy = q.Y * q.W; var yz = q.Y * q.Z; var wx = q.X * q.W;
        var s = pose.Scale;
        return new Matrix4x4(
            (1f - 2f * (yy + zz)) * s.X, 2f * (xy + wz) * s.X, 2f * (xz - wy) * s.X, 0f,
            2f * (xy - wz) * s.Y, (1f - 2f * (zz + xx)) * s.Y, 2f * (yz + wx) * s.Y, 0f,
            2f * (xz + wy) * s.Z, 2f * (yz - wx) * s.Z, (1f - 2f * (yy + xx)) * s.Z, 0f,
            pose.Translation.X, pose.Translation.Y, pose.Translation.Z, 1f);
    }
}
