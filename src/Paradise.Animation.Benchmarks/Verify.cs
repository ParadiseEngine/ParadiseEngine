using System.Numerics;

namespace Paradise.Animation.Benchmarks;

/// <summary>`dotnet run -c Release -- verify`: the ManagedSimd sampler against the blob runtime, pose for pose, before the benchmark is believed.</summary>
internal static class Verify
{
    public static void Run()
    {
        var rig = new Rig(64, seconds: 1.333f);
        using var blobSkeleton = OzzArchive.ReadSkeleton(rig.SkeletonArchive);
        using var blobClip = OzzArchive.ReadAnimation(rig.ClipArchive);
        using var blobContext = SamplingContext.Create(blobClip.Value.TrackCount);
        using var blobPoses = JointPoses.Create(blobClip.Value.TrackCount);
        var blobModels = new Matrix4x4[blobSkeleton.Value.JointCount];

        var managedSkeleton = Managed.Skeleton.Load(rig.SkeletonArchive);
        var managedClip = Managed.AnimationClip.Load(rig.ClipArchive);
        var simdContext = new ManagedSimd.SamplingContext(managedClip.TrackCount);
        var simdPoses = new ManagedSimd.SoaTransforms(managedClip.TrackCount);
        var simdModels = new Matrix4x4[managedSkeleton.JointCount];

        var random = new Random(7);
        var worstPose = 0f;
        var worstModel = 0f;
        for (var i = 0; i < 500; i++)
        {
            var ratio = i < 100 ? i / 100f : random.NextSingle();
            blobContext.Value.Sample(ref blobClip.Value, ratio, ref blobPoses.Value);
            LocalToModel.Compute(ref blobSkeleton.Value, ref blobPoses.Value, blobModels);
            simdContext.Sample(managedClip, ratio, simdPoses);
            ManagedSimd.LocalToModel.Compute(managedSkeleton, simdPoses, simdModels);

            var a = blobPoses.Value.ToArray();
            var b = simdPoses.ToArray();
            for (var j = 0; j < a.Length; j++)
            {
                worstPose = MathF.Max(worstPose, Vector3.Distance(a[j].Translation, b[j].Translation));
                worstPose = MathF.Max(worstPose, Vector3.Distance(a[j].Scale, b[j].Scale));
                var dr = a[j].Rotation - b[j].Rotation;
                worstPose = MathF.Max(worstPose, MathF.Max(MathF.Max(MathF.Abs(dr.X), MathF.Abs(dr.Y)), MathF.Max(MathF.Abs(dr.Z), MathF.Abs(dr.W))));
            }

            for (var j = 0; j < blobModels.Length; j++)
            {
                worstModel = MathF.Max(worstModel, MaxAbs(blobModels[j] - simdModels[j]));
            }
        }

        Console.WriteLine($"joints={blobSkeleton.Value.JointCount} worst pose delta={worstPose:E3} worst model delta={worstModel:E3}");
        Console.WriteLine(worstPose == 0f && worstModel == 0f ? "IDENTICAL to the blob runtime." : "DIFFERS — investigate before trusting the benchmark.");
    }

    private static float MaxAbs(Matrix4x4 m)
    {
        var max = 0f;
        foreach (var v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) max = MathF.Max(max, MathF.Abs(v));
        return max;
    }
}
