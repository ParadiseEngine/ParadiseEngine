using System.Numerics;

// Frozen copy of the managed-class runtime as committed in e4fa124, kept ONLY so the benchmark
// can measure it against the blob runtime that replaced it. Do not fix or extend; delete when the
// comparison stops being interesting.
namespace Paradise.Animation.Benchmarks.Managed;

using Paradise.Animation;

/// <summary>Turns local joint poses into model-space matrices by walking the skeleton's parents; ozz's <c>LocalToModelJob</c>.</summary>
public static class LocalToModel
{
    /// <summary>Row-vector convention: <c>model[i] = local[i] × model[parent]</c>, roots multiplied by <paramref name="root"/> (identity when null).</summary>
    /// <exception cref="ArgumentException">Fewer poses or outputs than joints.</exception>
    public static void Compute(Skeleton skeleton, ReadOnlySpan<JointPose> locals, Span<Matrix4x4> models, in Matrix4x4? root = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var count = skeleton.JointCount;
        if (locals.Length < count) throw new ArgumentException($"{locals.Length} poses for {count} joints.", nameof(locals));
        if (models.Length < count) throw new ArgumentException($"{models.Length} outputs for {count} joints.", nameof(models));

        var parents = skeleton.Parents;
        var rootMatrix = root ?? Matrix4x4.Identity;
        for (var i = 0; i < count; i++)
        {
            var parent = parents[i];
            models[i] = parent == Skeleton.NoParent
                ? locals[i].ToMatrix() * rootMatrix
                : locals[i].ToMatrix() * models[parent];
        }
    }
}
