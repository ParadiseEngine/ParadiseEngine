using System.Numerics;

using Paradise.Animation;
using Paradise.Animation.Offline;
using Paradise.Assets.Gltf;
using Paradise.Assets.Gltf.Test;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The golden test for the animation cook: a clip cooked to an ozz archive and sampled by
/// <see cref="SamplingContext"/> gives the joint palette the reference glTF sampler gives, on a
/// rig with an animated non-joint carrier above the mesh, a STEP channel, a scale channel and
/// three chained joints — within the bound ozz's quantization allows.
/// </summary>
public class AnimationGoldenTests
{
    /// <summary>What ozz's 16-bit keys cost on a metre-scale rig; a cook or sampler bug is orders of magnitude above it.</summary>
    private const float Tolerance = 1e-3f;

    private const float StepAt = 0.6f;

    private static byte[] RigGlb()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 0f, 1f, 0f, 0f, 2f, 0f], "VEC3");
        var jointsView = b.AddBufferView(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 2, 1, 0, 0 });
        var joints = b.AddAccessor(jointsView, GlbTestBuilder.UByte, "VEC4", 3);
        var weights = b.AddFloatAccessor([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0.5f, 0.5f, 0f, 0f], "VEC4");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, joints: joints, weights: weights));

        var meshNode = b.AddNode(mesh: mesh, skin: 0, name: "Body");                                              // 0
        var carrier = b.AddNode(translation: [0f, 0f, 1f], name: "carrier", children: [meshNode]);                  // 1, animated, not a joint
        var foot = b.AddNode(translation: [0f, 1f, 0f], name: "foot");                                              // 2
        var knee = b.AddNode(translation: [0f, 1f, 0f], rotation: [0f, 0f, 0.7071068f, 0.7071068f], name: "knee", children: [foot]); // 3
        var hip = b.AddNode(translation: [0f, 1f, 0f], scale: [1f, 1f, 1f], name: "hip", children: [knee]);          // 4

        var ibm = b.AddFloatAccessor([.. Translation(0, -1, 0), .. Translation(0, -2, 0), .. Translation(0.5f, -3, 0)], "MAT4");
        b.AddSkin([hip, knee, foot], ibm, name: "rig");

        var threeTimes = b.AddFloatAccessor([0f, 0.5f, 1f], "SCALAR");
        var twoTimes = b.AddFloatAccessor([0f, StepAt], "SCALAR");
        var endTimes = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var kneeTurns = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0.7071068f, 0.7071068f, 0f, 0f, 1f, 0f], "VEC4");
        var hipSteps = b.AddFloatAccessor([0f, 1f, 0f, 1f, 1f, 0f], "VEC3");
        var footGrows = b.AddFloatAccessor([1f, 1f, 1f, 2f, 2f, 2f], "VEC3");
        var carrierSlides = b.AddFloatAccessor([0f, 0f, 1f, 0f, 0f, 5f], "VEC3");
        b.AddAnimation("walk",
            (knee, "rotation", threeTimes, kneeTurns, null),
            (hip, "translation", twoTimes, hipSteps, "STEP"),
            (foot, "scale", endTimes, footGrows, "LINEAR"),
            (carrier, "translation", endTimes, carrierSlides, null));
        b.SetSceneRoots(carrier, hip);
        return b.Build();
    }

    private static float[] Translation(float x, float y, float z) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1];

    [Test]
    public async Task a_cooked_clip_samples_to_the_reference_palette()
    {
        var worst = WorstPaletteError(optimize: null);

        await Assert.That(worst).IsLessThan(Tolerance);
    }

    [Test]
    public async Task a_decimated_clip_stays_within_its_tolerance_on_this_rig()
    {
        var worst = WorstPaletteError(AnimationOptimizer.Setting.Default);

        await Assert.That(worst).IsLessThan(2e-2f);
    }

    [Test]
    public async Task the_reference_and_the_cook_disagree_on_a_rig_the_cook_got_wrong()
    {
        // A sanity check on the test itself: a clip cooked over a different skeleton order cannot pass.
        var asset = GltfSceneReader.ReadGeometry(RigGlb());
        var cooked = GltfCook.Cook(asset);
        using var skeleton = OzzArchive.ReadSkeleton(cooked.Skeleton!);
        var wrong = new ClipData(cooked.Clips[0].Name, cooked.Clips[0].Channels.Select(c => c with { Joint = (c.Joint + 1) % skeleton.Value.JointCount }).ToList());

        var worst = WorstPaletteError(asset, cooked, GltfCook.BuildClip(wrong, cooked.Skeleton!));

        await Assert.That(worst).IsGreaterThan(Tolerance);
    }

    private static float WorstPaletteError(AnimationOptimizer.Setting? optimize)
    {
        var asset = GltfSceneReader.ReadGeometry(RigGlb());
        var cooked = GltfCook.Cook(asset);
        var animation = GltfCook.BuildClip(cooked.Clips[0], cooked.Skeleton!, optimize);
        return WorstPaletteError(asset, cooked, animation);
    }

    private static float WorstPaletteError(GltfAsset asset, CookedGlb cooked, byte[] animationArchive)
    {
        using var skeletonBlob = OzzArchive.ReadSkeleton(cooked.Skeleton!);
        using var animationBlob = OzzArchive.ReadAnimation(animationArchive);
        ref var skeleton = ref skeletonBlob.Value;
        ref var animation = ref animationBlob.Value;
        var draw = cooked.Mesh.Draws[0];
        var skin = cooked.Mesh.Skin!;
        var rig = new GltfAnimationRig(asset);
        var clip = asset.Animations[0];
        var meshNode = asset.Instances[0].NodeIndex;

        using var contextBlob = SamplingContext.Create(animation.TrackCount);
        ref var context = ref contextBlob.Value;
        var locals = new JointPose[animation.TrackCount];
        var models = new Matrix4x4[skeleton.JointCount];
        var reference = new Matrix4x4[skin.Joints.Length];
        var worst = 0f;
        foreach (var ratio in new[] { 0f, 0.1f, 0.3f, 0.5f, 0.55f, 0.7f, 0.9f, 1f, 0.2f })
        {
            rig.EvaluatePose(clip, ratio * clip.Duration);
            rig.ComputeJointPalette(0, meshNode, reference);

            context.Sample(ref animation, ratio, locals);
            LocalToModel.Compute(ref skeleton, locals, models);
            if (!Matrix4x4.Invert(models[draw.NodeIndex], out var inverseMeshWorld)) inverseMeshWorld = Matrix4x4.Identity;
            for (var i = 0; i < skin.Joints.Length; i++)
            {
                var palette = skin.InverseBindMatrices[i] * models[skin.Joints[i]] * inverseMeshWorld;
                worst = MathF.Max(worst, MaxAbs(palette - reference[i]));
            }
        }

        return worst;
    }

    private static float MaxAbs(Matrix4x4 m)
    {
        var max = 0f;
        foreach (var v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) max = MathF.Max(max, MathF.Abs(v));
        return max;
    }
}
