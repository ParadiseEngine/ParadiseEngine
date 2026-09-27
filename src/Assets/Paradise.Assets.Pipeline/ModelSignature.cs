using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Paradise.Assets.Gltf;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// What of a model the pipeline binds and draws, compared between two GLBs of the same model — the
/// one a source was and the one it became — so a replacement that would change the build is told
/// from one that only re-encodes it.
/// </summary>
/// <remarks>
/// <para>
/// Draws are compared in scene order, since a prefab's material slots bind by it: triangle count,
/// bounds and material. A rigid draw's bounds are world bounds. A skinned draw's are its rest pose
/// through the skin — joint world × inverse bind — because glTF ignores the mesh node's transform
/// for a skinned mesh and the pipeline cooks skinned draws in bind space: an exporter may move the
/// mesh node's transform into the positions and reorder the joints, inverse binds to match, and
/// nothing drawn changes. Joints are compared as a set of names, each with its rest world
/// transform; clips by name, with when each starts and ends and how many keys its densest channel
/// has, so a clip resampled at another rate is told from the same one. The glTF order of clips, materials and images is kept besides, so
/// what is recorded by index can follow a reorder.
/// </para>
/// <para>
/// A material is compared by name, factors and each texture's bytes, which is what its extracted
/// document is made of. Every number is compared to within <see cref="Tolerance"/>.
/// </para>
/// </remarks>
internal sealed record ModelSignature(
    IReadOnlyList<ModelSignature.Draw> Draws,
    IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> Skins,
    IReadOnlyList<string> Animations,
    IReadOnlyList<string> ClipOrder,
    IReadOnlyDictionary<string, ModelSignature.ClipTiming> Clips,
    IReadOnlyList<string?> MaterialNames,
    IReadOnlyList<string?> ImageSha256)
{
    public const float Tolerance = 1e-3f;

    /// <summary>When a clip starts and ends, in seconds, and how many keys its densest channel has: a clip resampled at another rate keeps its name but not these.</summary>
    public sealed record ClipTiming(float Start, float End, int Keys);

    /// <summary>Keys per second of the first clip with a channel of two keys or more, its densest channel's; null for a model with no such clip.</summary>
    public float? KeyRate => Clips.Values.Where(clip => clip.Keys >= 2 && clip.End > clip.Start).Select(clip => (clip.Keys - 1) / (clip.End - clip.Start)).Cast<float?>().FirstOrDefault();

    /// <param name="Material">The draw's material, or null for none.</param>
    public sealed record Draw(int Triangles, Vector3 Min, Vector3 Max, MaterialData? Material);

    /// <param name="Textures">SHA-256 of each texture's image bytes (base colour, metallic-roughness, normal, occlusion, emissive), null for none.</param>
    public sealed record MaterialData(string? Name, float[] Factors, string AlphaMode, bool DoubleSided, string?[] Textures);

    /// <summary>Reads the signature of <paramref name="glb"/>; <paramref name="readUri"/> answers an external image's bytes by its uri, or null when there is none.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a GLB the pipeline reads.</exception>
    public static ModelSignature Read(byte[] glb, Func<string, byte[]?> readUri)
    {
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(readUri);
        if (!GlbBinary.TryRead(glb, out var gltf, out var bin)) throw new InvalidDataException("not a readable GLB");

        GltfAsset asset;
        try
        {
            asset = GltfSceneReader.ReadGeometry(glb);
        }
        catch (NotSupportedException error)
        {
            throw new InvalidDataException(error.Message, error);
        }

        var images = ImageHashes(gltf, bin, readUri);
        var materials = asset.Materials.Select(material => Material(material, images)).ToList();
        var worlds = NodeWorlds(asset.Nodes);

        var draws = new List<Draw>();
        foreach (var instance in asset.Instances)
        {
            var skin = instance.SkinIndex >= 0 && instance.SkinIndex < asset.Skins.Length ? asset.Skins[instance.SkinIndex] : null;
            foreach (var primitive in asset.Meshes[instance.MeshIndex].Primitives)
            {
                var (min, max) = skin is not null && primitive.JointsWeights is not null
                    ? RestBounds(primitive, skin, worlds)
                    : Bounds(primitive, instance.WorldTransform);
                var material = primitive.MaterialIndex >= 0 && primitive.MaterialIndex < materials.Count ? materials[primitive.MaterialIndex] : null;
                draws.Add(new Draw(primitive.Indices.Length / 3, min, max, material));
            }
        }

        var skins = asset.Skins
            .Select(skin => (IReadOnlyDictionary<string, Matrix4x4>)skin.JointNodes
                .Where(node => node >= 0 && node < asset.Nodes.Length)
                .GroupBy(node => asset.Nodes[node].Name ?? $"#{node}", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => worlds[group.First()], StringComparer.Ordinal))
            .ToList();

        return new ModelSignature(
            draws,
            skins,
            [.. asset.Animations.Select(animation => animation.Name ?? "").Order(StringComparer.Ordinal)],
            [.. asset.Animations.Select(animation => animation.Name ?? "")],
            asset.Animations
                .Where(animation => animation.Channels.Any(channel => channel.Times.Length > 0))
                .GroupBy(animation => animation.Name ?? "", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Timing(group.First()), StringComparer.Ordinal),
            [.. asset.Materials.Select(material => material.Name)],
            images);
    }

    /// <summary>Each way <paramref name="after"/> differs from <paramref name="before"/>, readable; empty when they match.</summary>
    public static IReadOnlyList<string> Differences(ModelSignature before, ModelSignature after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var differences = new List<string>();

        if (before.Draws.Count != after.Draws.Count)
        {
            differences.Add($"{before.Draws.Count} draw(s) became {after.Draws.Count}");
        }
        else
        {
            for (var i = 0; i < before.Draws.Count; i++)
            {
                if (DrawDifference(before.Draws[i], after.Draws[i]) is { } difference) differences.Add($"draw {i}: {difference}");
            }
        }

        if (before.Skins.Count != after.Skins.Count)
        {
            differences.Add($"{before.Skins.Count} skin(s) became {after.Skins.Count}");
        }
        else
        {
            for (var i = 0; i < before.Skins.Count; i++)
            {
                if (SkinDifference(before.Skins[i], after.Skins[i]) is { } difference) differences.Add($"skin {i}: {difference}");
            }
        }

        if (!before.Animations.SequenceEqual(after.Animations, StringComparer.Ordinal))
        {
            differences.Add($"clips [{string.Join(", ", before.Animations)}] became [{string.Join(", ", after.Animations)}]");
        }
        else
        {
            foreach (var (name, timing) in before.Clips)
            {
                if (after.Clips.GetValueOrDefault(name) is not { } resampled
                    || MathF.Abs(timing.Start - resampled.Start) > Tolerance || MathF.Abs(timing.End - resampled.End) > Tolerance || timing.Keys != resampled.Keys)
                {
                    var became = after.Clips.GetValueOrDefault(name) is { } other ? $"{other.Keys} keys over {other.Start:0.###}..{other.End:0.###}s" : "no keys";
                    differences.Add($"clip '{name}' had {timing.Keys} keys over {timing.Start:0.###}..{timing.End:0.###}s and has {became}");
                }
            }
        }

        return differences;
    }

    private static string? DrawDifference(Draw before, Draw after)
    {
        if (before.Triangles != after.Triangles) return $"{before.Triangles} triangles became {after.Triangles}";
        if (!Near(before.Min, after.Min) || !Near(before.Max, after.Max))
        {
            return $"bounds {Format(before.Min)}..{Format(before.Max)} became {Format(after.Min)}..{Format(after.Max)}";
        }

        return (before.Material, after.Material) switch
        {
            (null, null) => null,
            (null, { } added) => $"gained material '{added.Name}'",
            ({ } lost, null) => $"lost material '{lost.Name}'",
            ({ } a, { } b) => MaterialDifference(a, b),
        };
    }

    private static string? MaterialDifference(MaterialData before, MaterialData after)
    {
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal)) return $"material '{before.Name}' became '{after.Name}'";
        if (before.AlphaMode != after.AlphaMode || before.DoubleSided != after.DoubleSided
            || before.Factors.Zip(after.Factors).Any(pair => MathF.Abs(pair.First - pair.Second) > Tolerance))
        {
            return $"material '{before.Name}' changed its factors";
        }

        return before.Textures.SequenceEqual(after.Textures, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"material '{before.Name}' samples other texture bytes";
    }

    private static string? SkinDifference(IReadOnlyDictionary<string, Matrix4x4> before, IReadOnlyDictionary<string, Matrix4x4> after)
    {
        var missing = before.Keys.Where(name => !after.ContainsKey(name)).ToList();
        var added = after.Keys.Where(name => !before.ContainsKey(name)).ToList();
        if (missing.Count > 0 || added.Count > 0)
        {
            return $"joints lost [{string.Join(", ", missing)}] and gained [{string.Join(", ", added)}]";
        }

        var moved = before.Where(joint => !Near(joint.Value, after[joint.Key])).Select(joint => joint.Key).ToList();
        return moved.Count == 0 ? null : $"joints [{string.Join(", ", moved)}] moved at rest";
    }

    private static ClipTiming Timing(GltfAnimationData animation)
    {
        var keyed = animation.Channels.Where(channel => channel.Times.Length > 0).ToList();
        return new ClipTiming(keyed.Min(channel => channel.Times[0]), keyed.Max(channel => channel.Times[^1]), keyed.Max(channel => channel.Times.Length));
    }

    private static MaterialData Material(GltfMaterialData material, IReadOnlyList<string?> images)
    {
        string? Texture(int image) => image >= 0 && image < images.Count ? images[image] : null;
        return new MaterialData(
            material.Name,
            [
                material.BaseColorFactor.X, material.BaseColorFactor.Y, material.BaseColorFactor.Z, material.BaseColorFactor.W,
                material.MetallicFactor, material.RoughnessFactor,
                material.EmissiveFactor.X, material.EmissiveFactor.Y, material.EmissiveFactor.Z,
                material.NormalScale, material.OcclusionStrength, material.TransmissionFactor, material.AlphaCutoff,
                material.BaseColorUvTransform.Offset.X, material.BaseColorUvTransform.Offset.Y,
                material.BaseColorUvTransform.Scale.X, material.BaseColorUvTransform.Scale.Y, material.BaseColorUvTransform.Rotation,
            ],
            material.AlphaMode.ToString(),
            material.DoubleSided,
            [Texture(material.BaseColorImage), Texture(material.MetallicRoughnessImage), Texture(material.NormalImage), Texture(material.OcclusionImage), Texture(material.EmissiveImage)]);
    }

    /// <summary>SHA-256 of each image's bytes, embedded or external; null for one that cannot be read.</summary>
    private static List<string?> ImageHashes(JsonObject gltf, byte[] bin, Func<string, byte[]?> readUri)
    {
        var result = new List<string?>();
        if (gltf["images"] is not JsonArray images) return result;

        var views = gltf["bufferViews"] as JsonArray;
        foreach (var node in images)
        {
            byte[]? bytes = null;
            if (node is JsonObject image)
            {
                if (image["bufferView"] is JsonValue viewValue && viewValue.TryGetValue(out int view)
                    && views?[view] is JsonObject bufferView)
                {
                    var offset = bufferView["byteOffset"] is JsonValue o && o.TryGetValue(out int start) ? start : 0;
                    var length = bufferView["byteLength"] is JsonValue l && l.TryGetValue(out int count) ? count : 0;
                    if (offset >= 0 && length >= 0 && offset + length <= bin.Length) bytes = bin.AsSpan(offset, length).ToArray();
                }
                else if (image["uri"] is JsonValue uriValue && uriValue.TryGetValue(out string? uri) && !uri.StartsWith("data:", StringComparison.Ordinal))
                {
                    bytes = readUri(uri);
                }
            }

            result.Add(bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        return result;
    }

    /// <summary>Each node's rest world transform, row-vector: local (scale, rotation, translation) × parent world.</summary>
    private static Matrix4x4[] NodeWorlds(GltfNodeData[] nodes)
    {
        var worlds = new Matrix4x4[nodes.Length];
        var done = new bool[nodes.Length];

        Matrix4x4 World(int index)
        {
            // Parents first, iteratively: a deep chain must not overflow the stack.
            var chain = new Stack<int>();
            for (var at = index; at >= 0 && at < nodes.Length && !done[at]; at = nodes[at].ParentIndex) chain.Push(at);
            while (chain.Count > 0)
            {
                var node = chain.Pop();
                var local = Matrix4x4.CreateScale(nodes[node].RestScale)
                    * Matrix4x4.CreateFromQuaternion(nodes[node].RestRotation)
                    * Matrix4x4.CreateTranslation(nodes[node].RestTranslation);
                var parent = nodes[node].ParentIndex;
                worlds[node] = parent >= 0 && parent < nodes.Length ? local * worlds[parent] : local;
                done[node] = true;
            }

            return worlds[index];
        }

        for (var i = 0; i < nodes.Length; i++) World(i);
        return worlds;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(GltfPrimitive primitive, Matrix4x4 world)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var index in primitive.Indices)
        {
            var at = (int)index * GltfPrimitive.FloatsPerVertex;
            var point = Vector3.Transform(new Vector3(primitive.Vertices[at], primitive.Vertices[at + 1], primitive.Vertices[at + 2]), world);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        return (min, max);
    }

    private static (Vector3 Min, Vector3 Max) RestBounds(GltfPrimitive primitive, GltfSkinData skin, Matrix4x4[] worlds)
    {
        var palette = new Matrix4x4[skin.JointNodes.Length];
        for (var joint = 0; joint < palette.Length; joint++)
        {
            var node = skin.JointNodes[joint];
            var inverseBind = joint < skin.InverseBindMatrices.Length ? skin.InverseBindMatrices[joint] : Matrix4x4.Identity;
            palette[joint] = inverseBind * (node >= 0 && node < worlds.Length ? worlds[node] : Matrix4x4.Identity);
        }

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var weights = primitive.JointsWeights!;
        foreach (var index in primitive.Indices)
        {
            var at = (int)index * GltfPrimitive.FloatsPerVertex;
            var position = new Vector3(primitive.Vertices[at], primitive.Vertices[at + 1], primitive.Vertices[at + 2]);
            var skinAt = (int)index * GltfPrimitive.SkinFloatsPerVertex;
            var point = Vector3.Zero;
            for (var k = 0; k < 4; k++)
            {
                var joint = (int)weights[skinAt + k];
                var weight = weights[skinAt + 4 + k];
                if (weight == 0f || joint < 0 || joint >= palette.Length) continue;
                point += weight * Vector3.Transform(position, palette[joint]);
            }

            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        return (min, max);
    }

    private static bool Near(Vector3 a, Vector3 b)
        => MathF.Abs(a.X - b.X) <= Tolerance && MathF.Abs(a.Y - b.Y) <= Tolerance && MathF.Abs(a.Z - b.Z) <= Tolerance;

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                if (MathF.Abs(a[row, column] - b[row, column]) > Tolerance) return false;
            }
        }

        return true;
    }

    private static string Format(Vector3 value)
        => string.Create(CultureInfo.InvariantCulture, $"[{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}]");
}
