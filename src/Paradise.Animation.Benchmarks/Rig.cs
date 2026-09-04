using Paradise.Assets.Gltf;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Pipeline;

namespace Paradise.Animation.Benchmarks;

/// <summary>
/// The one source every implementation samples: a GLB from <c>PARADISE_BENCHMARK_GLB</c> when set
/// (a real character, say), else a procedural rig of the given size — a branching tree, every joint
/// animated on all three channels at 30 Hz, the shape a DCC export has. Cooked through the real
/// pipeline (<see cref="GltfCook"/>) so the archives are what the build would write.
/// </summary>
internal sealed class Rig
{
    public const string EnvironmentVariable = "PARADISE_BENCHMARK_GLB";

    public GltfAsset Asset { get; }
    public CookedGlb Cooked { get; }
    public byte[] SkeletonArchive { get; }
    public byte[] ClipArchive { get; }
    public GltfAnimationData Clip { get; }
    public int SkinIndex { get; }
    public int MeshNode { get; }

    public Rig(int joints, float seconds, string? clipName = null)
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var glb = !string.IsNullOrEmpty(path) && File.Exists(path) ? File.ReadAllBytes(path) : Procedural(joints, seconds);
        Asset = GltfSceneReader.ReadGeometry(glb);
        Cooked = GltfCook.Cook(Asset);
        SkeletonArchive = Cooked.Skeleton ?? throw new InvalidOperationException("The rig has no skeleton.");
        var clip = clipName is null ? Cooked.Clips[0] : Cooked.Clips.First(c => c.Name == clipName);
        ClipArchive = GltfCook.BuildClip(clip, SkeletonArchive);
        Clip = Asset.Animations.First(a => (a.Name ?? "") == clip.Name);
        var instance = Asset.Instances.First(i => i.SkinIndex >= 0);
        SkinIndex = instance.SkinIndex;
        MeshNode = instance.NodeIndex;
    }

    private static byte[] Procedural(int joints, float seconds)
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var jointsView = b.AddBufferView(new byte[12]);
        var jointAccessor = b.AddAccessor(jointsView, GlbTestBuilder.UByte, "VEC4", 3);
        var weights = b.AddFloatAccessor([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f], "VEC4");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, joints: jointAccessor, weights: weights));

        // Joint i parents to (i-1)/3: a tree three wide, like a spine with limbs. Built leaves-first
        // so every child index exists before its parent lists it.
        var random = new Random(7);
        var nodeOf = new int[joints];
        var children = new List<int>[joints];
        for (var i = 0; i < joints; i++) children[i] = [];
        for (var i = 1; i < joints; i++) children[(i - 1) / 3].Add(i);
        for (var i = joints - 1; i >= 0; i--)
        {
            nodeOf[i] = b.AddNode(
                translation: [0f, 0.2f + random.NextSingle() * 0.1f, 0f],
                rotation: [0f, 0f, 0f, 1f],
                name: $"joint{i}",
                children: children[i].Count == 0 ? null : children[i].Select(c => nodeOf[c]).ToArray());
        }

        var meshNode = b.AddNode(mesh: mesh, skin: 0, name: "Body");
        var identity = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var ibm = b.AddFloatAccessor(Enumerable.Range(0, joints).SelectMany(_ => identity).ToArray(), "MAT4");
        b.AddSkin(nodeOf, ibm, name: "rig");

        var frames = (int)MathF.Round(seconds * 30f) + 1;
        var times = b.AddFloatAccessor(Enumerable.Range(0, frames).Select(f => f / 30f).ToArray(), "SCALAR");
        var channels = new List<(int, string, int, int, string?)>();
        for (var i = 0; i < joints; i++)
        {
            var phase = random.NextSingle() * MathF.Tau;
            var rotations = new float[frames * 4];
            var translations = new float[frames * 3];
            var scales = new float[frames * 3];
            for (var f = 0; f < frames; f++)
            {
                var angle = 0.4f * MathF.Sin(f / 30f * MathF.Tau / seconds + phase);
                var q = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, angle);
                rotations[f * 4] = q.X; rotations[f * 4 + 1] = q.Y; rotations[f * 4 + 2] = q.Z; rotations[f * 4 + 3] = q.W;
                translations[f * 3 + 1] = 0.25f + 0.02f * MathF.Sin(f / 30f * MathF.Tau / seconds * 2f + phase);
                scales[f * 3] = scales[f * 3 + 1] = scales[f * 3 + 2] = 1f + 0.05f * MathF.Cos(f / 30f * MathF.Tau / seconds + phase);
            }

            channels.Add((nodeOf[i], "rotation", times, b.AddFloatAccessor(rotations, "VEC4"), null));
            channels.Add((nodeOf[i], "translation", times, b.AddFloatAccessor(translations, "VEC3"), null));
            channels.Add((nodeOf[i], "scale", times, b.AddFloatAccessor(scales, "VEC3"), null));
        }

        b.AddAnimation("Walk", [.. channels]);
        b.SetSceneRoots(nodeOf[0], meshNode);
        return b.Build();
    }
}
