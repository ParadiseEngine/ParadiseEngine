using System.Numerics;
using System.Text.Json;
using Paradise.Assets.Gltf.Json;

namespace Paradise.Assets.Gltf;

/// <summary>Entry point: decode a GLB into a <see cref="GltfAsset"/>. Scope = the Paradise
/// export contract — static triangle meshes, metallic-roughness materials,
/// KHR_texture_basisu / KHR_texture_transform. No animations, no skins, no sparse accessors,
/// no data-URI images; unsupported structure throws <see cref="NotSupportedException"/>,
/// malformed data throws <see cref="InvalidDataException"/>. Images are either embedded in the
/// BIN chunk or, via <see cref="Read(ReadOnlyMemory{byte}, Func{string, byte[]})"/>, resolved
/// from an external file URI relative to the GLB.</summary>
public static class GltfSceneReader
{
    /// <summary>Embedded-only: every image must live in the GLB's BIN chunk. External-URI images throw.</summary>
    public static GltfAsset Read(ReadOnlyMemory<byte> glb) => Read(glb, null);

    /// <summary>
    /// Reads a GLB. <paramref name="externalImageResolver"/>, when supplied, maps an image's
    /// external file URI (relative to the GLB) to its bytes — the external-KTX2 texture layout,
    /// where each texture is a sidecar <c>.ktx2</c> next to the GLB rather than embedded in the
    /// BIN chunk. When it is null, external-URI images throw, preserving the embedded-only
    /// contract. Resolved bytes must still be KTX2.
    /// </summary>
    public static GltfAsset Read(ReadOnlyMemory<byte> glb, Func<string, byte[]>? externalImageResolver)
    {
        var container = GlbContainer.Parse(glb);
        var root = JsonSerializer.Deserialize(container.Json.Span, GltfJsonContext.Default.GltfRoot)
            ?? throw new InvalidDataException("GLB JSON chunk deserialized to null.");
        var bin = container.Bin;

        var images = ReadImages(root, bin, externalImageResolver);
        var materials = ReadMaterials(root);
        var meshes = ReadMeshes(root, bin);
        var instances = BakeInstances(root);

        return new GltfAsset(instances, meshes, materials, images);
    }

    // -------- images --------

    private static GltfImageData[] ReadImages(GltfRoot root, ReadOnlyMemory<byte> bin, Func<string, byte[]>? externalImageResolver)
    {
        var srcImages = root.Images ?? [];
        var images = new GltfImageData[srcImages.Length];
        for (var i = 0; i < srcImages.Length; i++)
        {
            var src = srcImages[i];
            if (src.BufferView is not { } viewIndex)
            {
                images[i] = ReadExternalImage(i, src, externalImageResolver);
                continue;
            }

            var views = root.BufferViews
                ?? throw new InvalidDataException("glTF references a bufferView but declares none.");
            if ((uint)viewIndex >= (uint)views.Length)
                throw new InvalidDataException($"Image {i} bufferView {viewIndex} out of range.");
            var view = views[viewIndex];

            // Same embedded-buffer contract the accessor path enforces: an image bufferView
            // targeting an external buffer must throw, not silently slice the BIN chunk.
            var buffers = root.Buffers
                ?? throw new InvalidDataException($"Image {i} references buffer {view.Buffer} but glTF declares none.");
            if ((uint)view.Buffer >= (uint)buffers.Length)
                throw new InvalidDataException($"Image {i} buffer {view.Buffer} out of range ({buffers.Length} declared).");
            if (buffers[view.Buffer].Uri is not null)
                throw new NotSupportedException(
                    $"Image {i} lives in an external buffer ('{buffers[view.Buffer].Uri}') — the contract embeds " +
                    "everything in the GLB BIN chunk.");

            var offset = view.ByteOffset ?? 0;
            if (offset < 0 || view.ByteLength < 0 || offset + view.ByteLength > bin.Length)
                throw new InvalidDataException($"Image {i} bufferView exceeds the BIN chunk.");

            var bytes = bin.Slice(offset, view.ByteLength).ToArray();
            if (!IsKtx2(bytes))
            {
                // Magic sniff beats mimeType: ToktxKtx2 rewrites images in place and the magic
                // is what the decoder would actually face. The contract is KTX2-only — PNG/JPEG
                // GLBs mean the toktx pass didn't run on this asset.
                throw new NotSupportedException(
                    $"Image {i} is {DescribeImageMagic(bytes)}, but the contract requires KTX2 for every " +
                    "texture (KHR_texture_basisu). Run the toktx pass (ToktxKtx2.ConvertEmbeddedTextures) " +
                    "on this GLB before loading it.");
            }
            images[i] = new GltfImageData(bytes);
        }
        return images;
    }

    // An image with no bufferView is an external file URI (the external-KTX2 layout). Resolve it
    // via the caller-supplied resolver (which reads the sidecar relative to the GLB) and enforce
    // the same KTX2-only contract as the embedded path. data-URIs are out of scope.
    private static GltfImageData ReadExternalImage(int i, GltfImage src, Func<string, byte[]>? resolver)
    {
        if (src.Uri is not { } uri)
            throw new InvalidDataException($"Image {i} has neither a bufferView nor a uri.");
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Image {i} is a data-URI — only embedded bufferView images and external file URIs are supported.");
        if (resolver is null)
            throw new NotSupportedException(
                $"Image {i} has no bufferView (uri '{uri}') — external-URI images require an image resolver, " +
                "but this GLB is being read without one (embedded-only mode).");

        // glTF uris are percent-encoded per the spec (e.g. a space becomes %20) — decode before
        // handing off so the resolver sees the literal sidecar name, not the escaped form.
        var bytes = resolver(Uri.UnescapeDataString(uri));
        if (!IsKtx2(bytes))
            throw new NotSupportedException(
                $"External image {i} ('{uri}') is {DescribeImageMagic(bytes)}, but the contract requires KTX2 for " +
                "every texture (KHR_texture_basisu).");
        return new GltfImageData(bytes);
    }

    private static bool IsKtx2(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> ktx2Magic = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
        return bytes.Length >= ktx2Magic.Length && bytes[..ktx2Magic.Length].SequenceEqual(ktx2Magic);
    }

    private static string DescribeImageMagic(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            return "PNG";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "JPEG";
        return "an unrecognized format";
    }

    // -------- materials --------

    private static GltfMaterialData[] ReadMaterials(GltfRoot root)
    {
        var srcMaterials = root.Materials ?? [];
        var materials = new GltfMaterialData[srcMaterials.Length];
        for (var i = 0; i < srcMaterials.Length; i++)
        {
            var src = srcMaterials[i];
            var pbr = src.PbrMetallicRoughness;

            var baseColor = pbr?.BaseColorFactor is { Length: 4 } bc
                ? new Vector4(bc[0], bc[1], bc[2], bc[3])
                : Vector4.One;
            var emissive = src.EmissiveFactor is { Length: 3 } em
                ? new Vector3(em[0], em[1], em[2])
                : Vector3.Zero;

            var uvTransform = GltfUvTransform.Identity;
            if (pbr?.BaseColorTexture?.Extensions?.Transform is { } t)
            {
                uvTransform = new GltfUvTransform(
                    t.Offset is { Length: 2 } o ? new Vector2(o[0], o[1]) : Vector2.Zero,
                    t.Scale is { Length: 2 } s ? new Vector2(s[0], s[1]) : Vector2.One,
                    t.Rotation ?? 0f);
            }

            materials[i] = new GltfMaterialData(
                Name: src.Name,
                BaseColorFactor: baseColor,
                MetallicFactor: pbr?.MetallicFactor ?? 1f,
                RoughnessFactor: pbr?.RoughnessFactor ?? 1f,
                EmissiveFactor: emissive,
                NormalScale: src.NormalTexture?.Scale ?? 1f,
                OcclusionStrength: src.OcclusionTexture?.Strength ?? 1f,
                TransmissionFactor: src.Extensions?.Transmission?.TransmissionFactor ?? 0f,
                AlphaMode: src.AlphaMode switch
                {
                    null or "OPAQUE" => GltfAlphaMode.Opaque,
                    "MASK" => GltfAlphaMode.Mask,
                    "BLEND" => GltfAlphaMode.Blend,
                    var other => throw new InvalidDataException($"Material {i} has unknown alphaMode '{other}'."),
                },
                AlphaCutoff: src.AlphaCutoff ?? 0.5f,
                DoubleSided: src.DoubleSided,
                BaseColorImage: ResolveImage(root, pbr?.BaseColorTexture?.Index),
                MetallicRoughnessImage: ResolveImage(root, pbr?.MetallicRoughnessTexture?.Index),
                NormalImage: ResolveImage(root, src.NormalTexture?.Index),
                OcclusionImage: ResolveImage(root, src.OcclusionTexture?.Index),
                EmissiveImage: ResolveImage(root, src.EmissiveTexture?.Index),
                BaseColorUvTransform: uvTransform);
        }
        return materials;
    }

    /// <summary>Texture index → image index, preferring the KHR_texture_basisu source (the
    /// KTX2 payload ToktxKtx2 embeds) over the base source. −1 when absent.</summary>
    private static int ResolveImage(GltfRoot root, int? textureIndex)
    {
        if (textureIndex is not { } ti) return -1;
        var textures = root.Textures
            ?? throw new InvalidDataException("Material references a texture but glTF declares none.");
        if ((uint)ti >= (uint)textures.Length)
            throw new InvalidDataException($"Texture index {ti} out of range ({textures.Length} declared).");
        var texture = textures[ti];
        var image = texture.Extensions?.Basisu?.Source ?? texture.Source
            ?? throw new InvalidDataException($"Texture {ti} has neither a source nor a KHR_texture_basisu source.");

        var imageCount = root.Images?.Length ?? 0;
        if ((uint)image >= (uint)imageCount)
            throw new InvalidDataException($"Texture {ti} image {image} out of range ({imageCount} declared).");
        return image;
    }

    // -------- meshes --------

    private static GltfMeshData[] ReadMeshes(GltfRoot root, ReadOnlyMemory<byte> bin)
    {
        var srcMeshes = root.Meshes ?? [];
        var meshes = new GltfMeshData[srcMeshes.Length];
        for (var m = 0; m < srcMeshes.Length; m++)
        {
            var srcPrimitives = srcMeshes[m].Primitives ?? [];
            var primitives = new GltfPrimitive[srcPrimitives.Length];
            for (var p = 0; p < srcPrimitives.Length; p++)
            {
                primitives[p] = ReadPrimitive(root, bin, srcPrimitives[p], m, p);
            }
            meshes[m] = new GltfMeshData(srcMeshes[m].Name, primitives);
        }
        return meshes;
    }

    private static GltfPrimitive ReadPrimitive(
        GltfRoot root, ReadOnlyMemory<byte> bin, GltfPrimitiveJson primitive, int meshIndex, int primitiveIndex)
    {
        const int TrianglesMode = 4;
        if ((primitive.Mode ?? TrianglesMode) != TrianglesMode)
            throw new NotSupportedException(
                $"Mesh {meshIndex} primitive {primitiveIndex} has mode {primitive.Mode} — only TRIANGLES (4) is supported.");

        var attributes = primitive.Attributes
            ?? throw new InvalidDataException($"Mesh {meshIndex} primitive {primitiveIndex} has no attributes.");
        if (!attributes.TryGetValue("POSITION", out var positionAccessor))
            throw new InvalidDataException($"Mesh {meshIndex} primitive {primitiveIndex} has no POSITION attribute.");

        // Allocation ordering matters: every accessor's byte range is validated against its
        // bufferView BEFORE any array is sized from its count — the raw JSON count is untrusted
        // metadata, and a tiny GLB declaring count=200000000 must fail the range check, not
        // trigger a multi-GB allocation. The validated count is bounded by the BIN chunk size.
        var count = AccessorReader.GetValidatedFloatCount(root, bin, positionAccessor, 3);
        var hasNormals = attributes.TryGetValue("NORMAL", out var normalAccessor);
        if (hasNormals)
            RequireMatchingCount(
                AccessorReader.GetValidatedFloatCount(root, bin, normalAccessor, 3),
                count, meshIndex, primitiveIndex, "NORMAL");
        var hasTexCoords = attributes.TryGetValue("TEXCOORD_0", out var uvAccessor);
        if (hasTexCoords)
            RequireMatchingCount(
                AccessorReader.GetValidatedFloatCount(root, bin, uvAccessor, 2),
                count, meshIndex, primitiveIndex, "TEXCOORD_0");
        var hasTangents = attributes.TryGetValue("TANGENT", out var tangentAccessor);
        if (hasTangents)
            RequireMatchingCount(
                AccessorReader.GetValidatedFloatCount(root, bin, tangentAccessor, 4),
                count, meshIndex, primitiveIndex, "TANGENT");

        var positions = new float[count * 3];
        AccessorReader.ReadFloats(root, bin, positionAccessor, 3, positions);

        var normals = new float[count * 3];
        if (hasNormals)
        {
            AccessorReader.ReadFloats(root, bin, normalAccessor, 3, normals);
        }

        var uvs = new float[count * 2];
        if (hasTexCoords)
        {
            AccessorReader.ReadFloats(root, bin, uvAccessor, 2, uvs);
        }

        var tangents = new float[count * 4];
        if (hasTangents)
        {
            AccessorReader.ReadFloats(root, bin, tangentAccessor, 4, tangents);
        }

        var vertices = new float[count * GltfPrimitive.FloatsPerVertex];
        for (var i = 0; i < count; i++)
        {
            var w = i * GltfPrimitive.FloatsPerVertex;
            vertices[w + 0] = positions[i * 3 + 0];
            vertices[w + 1] = positions[i * 3 + 1];
            vertices[w + 2] = positions[i * 3 + 2];
            if (hasNormals)
            {
                vertices[w + 3] = normals[i * 3 + 0];
                vertices[w + 4] = normals[i * 3 + 1];
                vertices[w + 5] = normals[i * 3 + 2];
            }
            else
            {
                vertices[w + 4] = 1f; // default +Y — flat-lit rather than black
            }
            vertices[w + 6] = uvs[i * 2 + 0];
            vertices[w + 7] = uvs[i * 2 + 1];
            if (hasTangents)
            {
                vertices[w + 8] = tangents[i * 4 + 0];
                vertices[w + 9] = tangents[i * 4 + 1];
                vertices[w + 10] = tangents[i * 4 + 2];
                vertices[w + 11] = tangents[i * 4 + 3];
            }
            else
            {
                vertices[w + 8] = 1f;  // default +X tangent, +1 handedness — harmless without a
                vertices[w + 11] = 1f; // normal map (normal-mapped assets need DCC tangents)
            }
        }

        var indices = primitive.Indices is { } indexAccessor
            ? AccessorReader.ReadIndices(root, bin, indexAccessor)
            : BuildSequentialIndices(count);
        foreach (var index in indices)
        {
            if (index >= (uint)count)
                throw new InvalidDataException(
                    $"Mesh {meshIndex} primitive {primitiveIndex} index {index} exceeds vertex count {count}.");
        }

        return new GltfPrimitive(
            vertices, indices, primitive.Material ?? -1, hasNormals, hasTexCoords, hasTangents);
    }

    private static void RequireMatchingCount(
        int count, int expected, int meshIndex, int primitiveIndex, string attribute)
    {
        if (count != expected)
            throw new InvalidDataException(
                $"Mesh {meshIndex} primitive {primitiveIndex} attribute {attribute} has {count} elements " +
                $"but POSITION has {expected}.");
    }

    private static uint[] BuildSequentialIndices(int count)
    {
        var indices = new uint[count];
        for (var i = 0; i < indices.Length; i++) indices[i] = (uint)i;
        return indices;
    }

    // -------- scene graph --------

    private static GltfMeshInstance[] BakeInstances(GltfRoot root)
    {
        var nodes = root.Nodes ?? [];
        var scenes = root.Scenes ?? [];
        if (scenes.Length == 0)
        {
            return [];
        }

        var sceneIndex = root.Scene ?? 0;
        if ((uint)sceneIndex >= (uint)scenes.Length)
            throw new InvalidDataException($"Default scene {sceneIndex} out of range ({scenes.Length} declared).");

        // Iterative DFS (explicit stack): malformed input must never crash via CLR stack
        // overflow — a 100k-node single-child chain is a VALID tree and must load. The visit
        // budget (nodes.Length) rejects both cycles and shared children (glTF node graphs must
        // be trees) with a typed error.
        var instances = new List<GltfMeshInstance>();
        var pending = new Stack<(int NodeIndex, Matrix4x4 ParentWorld)>();
        var sceneRoots = scenes[sceneIndex].Nodes ?? [];
        for (var i = sceneRoots.Length - 1; i >= 0; i--)
        {
            pending.Push((sceneRoots[i], Matrix4x4.Identity));
        }

        var visitBudget = nodes.Length;
        while (pending.Count > 0)
        {
            var (nodeIndex, parentWorld) = pending.Pop();
            if ((uint)nodeIndex >= (uint)nodes.Length)
                throw new InvalidDataException($"Node index {nodeIndex} out of range ({nodes.Length} declared).");
            if (visitBudget-- <= 0)
                throw new InvalidDataException(
                    "Scene visits more nodes than the glTF declares — the node graph has a cycle or a " +
                    "shared child, but glTF node graphs must be trees.");

            var node = nodes[nodeIndex];
            // Row-vector convention: world = local × parent. glTF's column-major matrix loads
            // byte-identically into System.Numerics' row-major storage (the classic transpose
            // duality), so no element shuffling happens anywhere.
            var world = LocalMatrix(node, nodeIndex) * parentWorld;

            if (node.Mesh is { } meshIndex)
            {
                var meshCount = root.Meshes?.Length ?? 0;
                if ((uint)meshIndex >= (uint)meshCount)
                    throw new InvalidDataException($"Node {nodeIndex} mesh {meshIndex} out of range ({meshCount} declared).");
                instances.Add(new GltfMeshInstance(meshIndex, world, node.Name));
            }

            var children = node.Children ?? [];
            for (var i = children.Length - 1; i >= 0; i--)
            {
                pending.Push((children[i], world)); // reversed push keeps document order
            }
        }
        return instances.ToArray();
    }

    private static Matrix4x4 LocalMatrix(GltfNode node, int nodeIndex)
    {
        if (node.Matrix is { } m)
        {
            if (m.Length != 16)
                throw new InvalidDataException($"Node {nodeIndex} matrix has {m.Length} elements, expected 16.");
            return new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        var scale = node.Scale is { Length: 3 } s ? new Vector3(s[0], s[1], s[2]) : Vector3.One;
        var rotation = node.Rotation is { Length: 4 } r ? new Quaternion(r[0], r[1], r[2], r[3]) : Quaternion.Identity;
        var translation = node.Translation is { Length: 3 } t ? new Vector3(t[0], t[1], t[2]) : Vector3.Zero;

        // glTF composes column-vector M = T·R·S; in row-vector convention that is S·R·T.
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation);
    }
}
