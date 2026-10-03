using System.Numerics;
using System.Security.Cryptography;

using Paradise.Animation;
using Paradise.Animation.Offline;
using Paradise.Assets.Gltf;
using Paradise.Assets.Mesh;

namespace Paradise.Assets.Pipeline;

/// <summary>What one GLB cooks to: the mesh blob, the skeleton (as an ozz archive) when the file has skins or clips, its clips over that skeleton's joints, and which glTF material each draw slot bound (for the material documents).</summary>
/// <param name="Skeleton">The <c>ozz-skeleton</c> archive, what the build writes for a <c>.skeleton</c> document; open it with <see cref="OzzArchive.ReadSkeleton(System.ReadOnlySpan{byte})"/>.</param>
/// <param name="SlotMaterials">Per draw slot, the glTF material index the primitive named, or −1. Slot <c>i</c> is glTF primitive <c>i</c> in scene order — the material-slot contract.</param>
public sealed record CookedGlb(MeshData Mesh, byte[]? Skeleton, IReadOnlyList<ClipData> Clips, int[] SlotMaterials);

/// <summary>
/// Cooks a decoded GLB's default scene into mesh data and ozz skeleton/clip inputs for tooling.
/// Rigid draws bake their node's world transform into the vertices (one model
/// matrix per entity is then the whole placement); skinned draws stay in bind space with joints
/// and weights interleaved, and the mesh carries its skin. The skeleton is the WHOLE node tree
/// in ozz's depth-first order, and skins, clips and draws address joints by that index.
/// </summary>
/// <remarks>
/// Builds, extraction and geometry baking share this cook; runtime loaders consume its cooked outputs.
/// Order is normative: draws are
/// the scene's instances in order, each instance's primitives in order — the slot a material
/// document binds to. The tree is kept whole rather than trimmed to skin joints because a clip
/// may animate a node that is not a joint (a root-motion carrier, the mesh's own node), and the
/// palette needs every ancestor. Children follow their parent in ascending glTF node order.
/// </remarks>
public static class GltfCook
{
    /// <summary>Seconds between the i-frames a cooked clip carries; a loop restart or a scrub then seeks instead of walking the key stream from the start.</summary>
    public const float IframeInterval = 1f;

    /// <exception cref="InvalidDataException">The decoded model exceeds the supported skin or skeleton limits, or has an invalid node hierarchy.</exception>
    public static CookedGlb Cook(GltfAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Skins.Length > 1)
        {
            throw new InvalidDataException($"The GLB has {asset.Skins.Length} skins; a Paradise mesh carries one skeleton. Split the file, one rig per GLB.");
        }

        // The skeleton is the whole node tree, and ozz caps it; a rigid GLB with no skin or clip
        // builds no skeleton and may have any number of nodes.
        if ((asset.Skins.Length > 0 || asset.Animations.Length > 0) && asset.Nodes.Length > SkeletonBlob.MaxJoints)
        {
            throw new InvalidDataException($"The GLB has {asset.Nodes.Length} nodes; a skeleton holds at most {SkeletonBlob.MaxJoints}. Trim the node tree or split the file.");
        }

        var skinned = asset.Instances.Any(instance =>
            instance.SkinIndex >= 0 && asset.Meshes[instance.MeshIndex].Primitives.Any(p => p.JointsWeights is not null));
        var layout = skinned ? MeshVertexLayout.Skinned : MeshVertexLayout.Static;
        var stride = skinned ? MeshBlob.SkinnedFloatsPerVertex : MeshBlob.StaticFloatsPerVertex;

        var jointOf = asset.Skins.Length == 0 && asset.Animations.Length == 0 ? null : JointOrder(asset.Nodes);

        var vertices = new List<float>();
        var indices = new List<uint>();
        var draws = new List<MeshDrawData>();
        var slotMaterials = new List<int>();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        foreach (var instance in asset.Instances)
        {
            foreach (var primitive in asset.Meshes[instance.MeshIndex].Primitives)
            {
                var isSkinnedDraw = instance.SkinIndex >= 0 && primitive.JointsWeights is not null;
                var source = isSkinnedDraw ? primitive.Vertices : BakeTransform(primitive.Vertices, instance.WorldTransform);
                var vertexBase = (uint)(vertices.Count / stride);
                var count = primitive.VertexCount;

                for (var v = 0; v < count; v++)
                {
                    var at = v * GltfPrimitive.FloatsPerVertex;
                    for (var f = 0; f < GltfPrimitive.FloatsPerVertex; f++) vertices.Add(source[at + f]);
                    if (skinned)
                    {
                        var skin = primitive.JointsWeights;
                        var skinAt = v * GltfPrimitive.SkinFloatsPerVertex;
                        for (var f = 0; f < GltfPrimitive.SkinFloatsPerVertex; f++) vertices.Add(isSkinnedDraw ? skin![skinAt + f] : 0f);
                    }

                    var position = new Vector3(source[at], source[at + 1], source[at + 2]);
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }

                var first = (uint)indices.Count;
                var corners = primitive.Indices;
                // A mirrored node (negative determinant) turns its triangles inside out once its
                // transform is baked in; the second and third corner swap back, so the draw faces
                // where the author saw it and the navmesh bake, which reads this blob, sees it
                // walkable. A skinned draw stays in bind space, where the skin moves it at runtime.
                if (!isSkinnedDraw && instance.WorldTransform.GetDeterminant() < 0f)
                {
                    var corner = 0;
                    for (; corner + 2 < corners.Length; corner += 3)
                    {
                        indices.Add(vertexBase + corners[corner]);
                        indices.Add(vertexBase + corners[corner + 2]);
                        indices.Add(vertexBase + corners[corner + 1]);
                    }

                    // A malformed trailing one or two corners are kept as they are, so the draw's
                    // index count is the primitive's either way.
                    for (; corner < corners.Length; corner++) indices.Add(vertexBase + corners[corner]);
                }
                else
                {
                    foreach (var index in corners) indices.Add(vertexBase + index);
                }

                draws.Add(new MeshDrawData(
                    first, (uint)primitive.Indices.Length, draws.Count,
                    isSkinnedDraw ? jointOf![instance.NodeIndex] : -1,
                    isSkinnedDraw ? instance.SkinIndex : -1,
                    instance.NodeName));
                slotMaterials.Add(primitive.MaterialIndex);
            }
        }

        if (draws.Count == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }

        var meshSkin = skinned && jointOf is not null
            ? new MeshSkinData(asset.Skins[0].JointNodes.Select(node => jointOf[node]).ToArray(), asset.Skins[0].InverseBindMatrices)
            : null;
        var mesh = new MeshData(layout, [.. vertices], [.. indices], draws, min, max, meshSkin);
        var skeleton = jointOf is null ? null : BuildSkeleton(asset.Nodes, jointOf);
        var clips = asset.Animations.Select((clip, i) => Clip(clip, i, jointOf!)).ToList();
        return new CookedGlb(mesh, skeleton, clips, [.. slotMaterials]);
    }

    /// <summary>Cooks a clip over the skeleton it was cooked with: rest pose on unanimated joints, STEP baked, optionally decimated, then compressed to an <c>ozz-animation</c> archive.</summary>
    /// <param name="skeletonArchive">The <c>ozz-skeleton</c> archive the clip's joints index, <see cref="CookedGlb.Skeleton"/>.</param>
    /// <param name="optimize">Optional key decimation; null skips optimization, while conversion still bakes STEP holds and rotation keys before quantization.</param>
    public static byte[] BuildClip(ClipData clip, ReadOnlySpan<byte> skeletonArchive, AnimationOptimizer.Setting? optimize = null)
    {
        using var skeleton = OzzArchive.ReadSkeleton(skeletonArchive);
        var raw = ClipConverter.ToRaw(clip, ref skeleton.Value);
        if (optimize is { } setting) raw = AnimationOptimizer.Optimize(raw, ref skeleton.Value, setting);
        using var built = AnimationBuilder.Build(raw, IframeInterval);
        return OzzArchive.WriteAnimation(ref built.Value);
    }

    /// <summary>Per glTF node, its joint index: depth-first, parents first, siblings by ascending node index — the order ozz's builder would give the same tree.</summary>
    internal static int[] JointOrder(GltfNodeData[] nodes)
    {
        var children = new List<int>[nodes.Length];
        for (var i = 0; i < nodes.Length; i++) children[i] = [];
        var roots = new List<int>();
        for (var i = 0; i < nodes.Length; i++)
        {
            var parent = nodes[i].ParentIndex;
            if (parent < 0) roots.Add(i);
            else if (parent < nodes.Length) children[parent].Add(i);
            else throw new InvalidDataException($"Node {i} has parent {parent}, outside the tree.");
        }

        var jointOf = new int[nodes.Length];
        Array.Fill(jointOf, -1);
        var next = 0;
        var stack = new Stack<int>();
        for (var r = roots.Count - 1; r >= 0; r--) stack.Push(roots[r]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (jointOf[node] >= 0) throw new InvalidDataException($"Node {node} is reached twice; the node graph is not a tree.");
            jointOf[node] = next++;
            for (var c = children[node].Count - 1; c >= 0; c--) stack.Push(children[node][c]);
        }

        if (next != nodes.Length) throw new InvalidDataException("A node's parent chain never reaches a scene root.");
        return jointOf;
    }

    private static byte[] BuildSkeleton(GltfNodeData[] nodes, int[] jointOf)
    {
        var count = nodes.Length;
        var names = new string[count];
        var parents = new short[count];
        var poses = new JointPose[count];
        for (var node = 0; node < count; node++)
        {
            var joint = jointOf[node];
            names[joint] = nodes[node].Name ?? "";
            parents[joint] = nodes[node].ParentIndex < 0 ? SkeletonBlob.NoParent : (short)jointOf[nodes[node].ParentIndex];
            poses[joint] = new JointPose(nodes[node].RestTranslation, nodes[node].RestRotation, nodes[node].RestScale).WithNormalizedRotation();
        }

        using var skeleton = SkeletonBlob.Create(names, parents, poses);
        return OzzArchive.WriteSkeleton(ref skeleton.Value);
    }

    private static ClipData Clip(GltfAnimationData clip, int index, int[] jointOf)
    {
        var channels = clip.Channels.Select(channel => new ClipChannelData(
            jointOf[channel.NodeIndex],
            channel.Path switch
            {
                GltfAnimationPath.Translation => ChannelPath.Translation,
                GltfAnimationPath.Rotation => ChannelPath.Rotation,
                GltfAnimationPath.Scale => ChannelPath.Scale,
                _ => throw new InvalidDataException($"Clip '{clip.Name}' animates an unsupported path."),
            },
            channel.Step,
            channel.Times,
            channel.Values)).ToList();
        return new ClipData(clip.Name ?? $"clip_{index}", channels);
    }

    /// <summary>SHA-256 of the clip's blob with channels ordered by joint and path and the name left out, so re-export ordering and DCC renames preserve its identity.</summary>
    public static string ClipFingerprint(ClipData clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        // Blender can emit equivalent channels in a different order on each export.
        var canonical = clip with
        {
            Name = "",
            Channels = clip.Channels.OrderBy(channel => channel.Joint).ThenBy(channel => channel.Path).ToArray(),
        };
        return Convert.ToHexStringLower(SHA256.HashData(ClipFormat.Write(canonical)));
    }

    /// <summary>Transforms positions and tangents by the node matrix and normals by its cofactor matrix, normalizing direction vectors and preserving UVs and tangent signs.</summary>
    private static float[] BakeTransform(float[] vertices, in Matrix4x4 transform)
    {
        if (transform.IsIdentity) return vertices;

        var baked = new float[vertices.Length];
        var normalMatrix = Cofactor(transform);

        for (var i = 0; i < vertices.Length; i += GltfPrimitive.FloatsPerVertex)
        {
            var position = Vector3.Transform(new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]), transform);
            var normal = NormalizeOrZero(Vector3.TransformNormal(new Vector3(vertices[i + 3], vertices[i + 4], vertices[i + 5]), normalMatrix));
            var tangent = NormalizeOrZero(Vector3.TransformNormal(new Vector3(vertices[i + 8], vertices[i + 9], vertices[i + 10]), transform));

            baked[i] = position.X; baked[i + 1] = position.Y; baked[i + 2] = position.Z;
            baked[i + 3] = normal.X; baked[i + 4] = normal.Y; baked[i + 5] = normal.Z;
            baked[i + 6] = vertices[i + 6]; baked[i + 7] = vertices[i + 7];
            baked[i + 8] = tangent.X; baked[i + 9] = tangent.Y; baked[i + 10] = tangent.Z;
            baked[i + 11] = vertices[i + 11];
        }

        return baked;
    }

    /// <summary>
    /// The determinant times the inverse-transpose when invertible, preserving orientation under
    /// reflection and remaining defined for singular transforms such as a collapsed scale axis.
    /// </summary>
    private static Matrix4x4 Cofactor(in Matrix4x4 m) => new(
        m.M22 * m.M33 - m.M23 * m.M32, m.M23 * m.M31 - m.M21 * m.M33, m.M21 * m.M32 - m.M22 * m.M31, 0f,
        m.M13 * m.M32 - m.M12 * m.M33, m.M11 * m.M33 - m.M13 * m.M31, m.M12 * m.M31 - m.M11 * m.M32, 0f,
        m.M12 * m.M23 - m.M13 * m.M22, m.M13 * m.M21 - m.M11 * m.M23, m.M11 * m.M22 - m.M12 * m.M21, 0f,
        0f, 0f, 0f, 1f);

    /// <summary>A vector a degenerate transform collapsed has no direction; zero is what a shader can clamp, NaN is not.</summary>
    private static Vector3 NormalizeOrZero(Vector3 v)
    {
        var lengthSquared = v.LengthSquared();
        return lengthSquared > 1e-12f ? v / MathF.Sqrt(lengthSquared) : Vector3.Zero;
    }
}
