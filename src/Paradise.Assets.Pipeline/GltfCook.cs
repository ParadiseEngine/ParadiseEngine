using System.Numerics;

using Paradise.Animation;
using Paradise.Assets.Gltf;
using Paradise.Assets.Mesh;

namespace Paradise.Assets.Pipeline;

/// <summary>What one GLB cooks to: the mesh blob, the skeleton when the file has skins, its clips, and which glTF material each draw slot bound (for the material documents).</summary>
/// <param name="SlotMaterials">Per draw slot, the glTF material index the primitive named, or −1. Slot <c>i</c> is glTF primitive <c>i</c> in scene order — the material-slot contract.</param>
public sealed record CookedGlb(MeshData Mesh, SkeletonData? Skeleton, IReadOnlyList<ClipData> Clips, int[] SlotMaterials);

/// <summary>
/// Turns a GLB's default scene into Paradise blobs, once, at extraction: the runtime never sees
/// glTF. Rigid draws bake their node's world transform into the vertices (one model matrix per
/// entity is then the whole placement); skinned draws stay in bind space with joints and weights
/// interleaved, and name the skin and node their palette is computed for.
/// </summary>
/// <remarks>
/// This is the same walk the hosts did at load time in <c>SceneAssets.Upload</c>, moved to the
/// pipeline so it runs once per export instead of once per launch. Order is normative: draws are
/// the scene's instances in order, each instance's primitives in order — the slot a material
/// document binds to.
/// </remarks>
public static class GltfCook
{
    /// <exception cref="InvalidDataException">The GLB is not one this cook can represent: two skins, or a cubic-spline clip.</exception>
    public static CookedGlb Cook(GltfAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Skins.Length > 1)
        {
            throw new InvalidDataException($"The GLB has {asset.Skins.Length} skins; a Paradise mesh carries one skeleton. Split the file, one rig per GLB.");
        }

        var skinned = asset.Instances.Any(instance =>
            instance.SkinIndex >= 0 && asset.Meshes[instance.MeshIndex].Primitives.Any(p => p.JointsWeights is not null));
        var layout = skinned ? MeshVertexLayout.Skinned : MeshVertexLayout.Static;
        var stride = skinned ? MeshBlob.SkinnedFloatsPerVertex : MeshBlob.StaticFloatsPerVertex;

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
                foreach (var index in primitive.Indices) indices.Add(vertexBase + index);

                draws.Add(new MeshDrawData(
                    first, (uint)primitive.Indices.Length, draws.Count,
                    isSkinnedDraw ? instance.NodeIndex : -1,
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

        var mesh = new MeshData(layout, [.. vertices], [.. indices], draws, min, max);
        var skeleton = asset.Skins.Length == 0 && asset.Animations.Length == 0 ? null : Skeleton(asset);
        var clips = asset.Animations.Select((clip, i) => Clip(clip, i)).ToList();
        return new CookedGlb(mesh, skeleton, clips, [.. slotMaterials]);
    }

    /// <summary>The node tree with rest pose, plus skins — even for a GLB with clips and no skin, since a clip addresses nodes.</summary>
    private static SkeletonData Skeleton(GltfAsset asset)
    {
        var nodes = asset.Nodes
            .Select(node => new SkeletonNodeData(node.Name, node.ParentIndex, node.RestTranslation, node.RestRotation, node.RestScale))
            .ToList();
        var skins = asset.Skins
            .Select(skin => new SkinData(skin.Name, skin.JointNodes, skin.InverseBindMatrices))
            .ToList();
        return new SkeletonData(nodes, skins);
    }

    private static ClipData Clip(GltfAnimationData clip, int index)
    {
        var channels = clip.Channels.Select(channel => new ClipChannelData(
            channel.NodeIndex,
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

    /// <summary>Positions through the matrix, normals through its inverse-transpose (non-uniform scale would shear them off the surface otherwise), tangents through the matrix; uv and tangent sign pass through.</summary>
    private static float[] BakeTransform(float[] vertices, in Matrix4x4 transform)
    {
        if (transform.IsIdentity) return vertices;

        var baked = new float[vertices.Length];
        Matrix4x4.Invert(transform, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);

        for (var i = 0; i < vertices.Length; i += GltfPrimitive.FloatsPerVertex)
        {
            var position = Vector3.Transform(new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]), transform);
            var normal = Vector3.Normalize(Vector3.TransformNormal(new Vector3(vertices[i + 3], vertices[i + 4], vertices[i + 5]), normalMatrix));
            var tangent = Vector3.TransformNormal(new Vector3(vertices[i + 8], vertices[i + 9], vertices[i + 10]), transform);

            baked[i] = position.X; baked[i + 1] = position.Y; baked[i + 2] = position.Z;
            baked[i + 3] = normal.X; baked[i + 4] = normal.Y; baked[i + 5] = normal.Z;
            baked[i + 6] = vertices[i + 6]; baked[i + 7] = vertices[i + 7];
            baked[i + 8] = tangent.X; baked[i + 9] = tangent.Y; baked[i + 10] = tangent.Z;
            baked[i + 11] = vertices[i + 11];
        }

        return baked;
    }
}
