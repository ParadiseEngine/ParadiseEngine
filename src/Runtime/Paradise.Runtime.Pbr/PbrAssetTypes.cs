using System.Numerics;
using Paradise.Assets.Mesh;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Zio;

namespace Paradise.Runtime.Pbr;

/// <summary>A cooked mesh made resident: its uploaded primitives, and the skin a palette is staged from.</summary>
/// <param name="Path">The content path it was loaded from.</param>
/// <param name="Mesh">One primitive per cooked draw, in draw order.</param>
/// <param name="Skin">The skin of a skinned layout, or null.</param>
/// <param name="MeshJoints">Per primitive, the skeleton joint its draw sat on (<c>MeshDraw.NodeIndex</c>), or -1
/// for a rigid draw.</param>
/// <remarks>Rigid draws are baked into the model's space and skinned draws stay in bind space, so one
/// instance with the model's placement as <see cref="PbrInstance.Model"/> draws both correctly when its
/// palette is staged in model space (<see cref="JointPaletteStager.Stage(Paradise.Animation.AnimationPlayer, PbrMeshAsset)"/>).
/// <see cref="MeshJoints"/> is for a game that instead places a skinned draw at its own node, with that
/// node's model matrix in the instance's placement and its joint as the palette's mesh joint.</remarks>
public sealed record PbrMeshAsset(UPath Path, PbrMesh Mesh, MeshSkinData? Skin, IReadOnlyList<int> MeshJoints);

/// <summary>Loads cooked mesh blobs (<see cref="MeshBlobFormat"/>) into <see cref="PbrMeshAsset"/>s.</summary>
/// <remarks>Decoding runs on the loader's worker and splits the blob into one vertex stream per draw, in
/// the layout the draw renders with: 20 floats for a skinned draw, the 12-float static layout for a rigid
/// one, even inside a skinned blob (whose rigid draws carry zero weights). Each stream holds only the
/// vertices its draw indexes. The upload then makes one primitive per draw on the render thread. Each draw
/// binds the material <paramref name="materialFor"/> picks for its slot, or a white default.</remarks>
/// <param name="renderer">The renderer primitives are uploaded to; the loader's thread must be its render thread.</param>
/// <param name="materialFor">Maps (mesh path, draw material slot) to a material id; null uses the default.</param>
public sealed class PbrMeshAssetType(PbrRenderer renderer, Func<UPath, int, int>? materialFor = null)
    : IAssetType<PbrMeshAssetType.Cooked, PbrMeshAsset>
{
    private int _defaultMaterial = -1;

    /// <summary>One draw, ready to upload: its own vertices in the layout it renders with, and indices into them.</summary>
    /// <param name="MeshJoint">The draw's <c>NodeIndex</c>; -1 for a rigid draw.</param>
    public sealed record CookedDraw(float[] Vertices, uint[] Indices, bool Skinned, int MaterialSlot, int MeshJoint);

    /// <summary>A decoded blob, waiting for its upload.</summary>
    public sealed record Cooked(UPath Path, IReadOnlyList<CookedDraw> Draws, MeshSkinData? Skin);

    public Cooked Decode(UPath path, byte[] bytes) => Split(path, MeshBlobFormat.Read(bytes));

    /// <summary>Splits a blob into per-draw streams in each draw's own layout.</summary>
    /// <exception cref="InvalidDataException">A draw's index range or an index lies outside the blob.</exception>
    public static Cooked Split(UPath path, MeshData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sourceStride = data.FloatsPerVertex;
        var vertexCount = data.VertexCount;
        var remap = new int[vertexCount];
        Array.Fill(remap, -1);
        var order = new List<int>();
        var draws = new CookedDraw[data.Draws.Count];
        for (var d = 0; d < draws.Length; d++)
        {
            var draw = data.Draws[d];
            if ((ulong)draw.FirstIndex + draw.IndexCount > (ulong)data.Indices.Length)
                throw new InvalidDataException($"Draw {d} of '{path}' reads indices {draw.FirstIndex}+{draw.IndexCount} of {data.Indices.Length}.");

            var skinned = data.Layout == MeshVertexLayout.Skinned && draw.SkinIndex >= 0;
            var stride = skinned ? MeshBlob.SkinnedFloatsPerVertex : MeshBlob.StaticFloatsPerVertex;
            var source = data.Indices.AsSpan((int)draw.FirstIndex, (int)draw.IndexCount);
            var indices = new uint[source.Length];
            order.Clear();
            for (var i = 0; i < source.Length; i++)
            {
                var index = source[i];
                if (index >= (uint)vertexCount)
                    throw new InvalidDataException($"Draw {d} of '{path}' indexes vertex {index} of {vertexCount}.");
                if (remap[index] < 0)
                {
                    remap[index] = order.Count;
                    order.Add((int)index);
                }

                indices[i] = (uint)remap[index];
            }

            // Only the vertices this draw uses, in first-use order, each cut to the draw's layout.
            var vertices = new float[order.Count * stride];
            for (var v = 0; v < order.Count; v++)
            {
                data.Vertices.AsSpan(order[v] * sourceStride, stride).CopyTo(vertices.AsSpan(v * stride));
                remap[order[v]] = -1;
            }

            draws[d] = new CookedDraw(vertices, indices, skinned, draw.MaterialSlot, skinned ? draw.NodeIndex : -1);
        }

        return new Cooked(path, draws, data.Skin);
    }

    public PbrMeshAsset Upload(Cooked decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        var primitives = new PbrPrimitive[decoded.Draws.Count];
        var meshJoints = new int[primitives.Length];
        var uploaded = 0;
        try
        {
            for (; uploaded < primitives.Length; uploaded++)
            {
                var draw = decoded.Draws[uploaded];
                var material = Material(decoded.Path, draw.MaterialSlot);
                primitives[uploaded] = draw.Skinned
                    ? renderer.UploadSkinnedPrimitive(draw.Vertices, draw.Indices, material)
                    : renderer.UploadPrimitive(draw.Vertices, draw.Indices, material);
                meshJoints[uploaded] = draw.MeshJoint;
            }
        }
        catch
        {
            for (var i = 0; i < uploaded; i++) renderer.ReleasePrimitive(primitives[i]);
            throw;
        }

        return new PbrMeshAsset(decoded.Path, new PbrMesh(primitives), decoded.Skin, meshJoints);
    }

    public void Unload(PbrMeshAsset resident)
    {
        ArgumentNullException.ThrowIfNull(resident);
        foreach (var primitive in resident.Mesh.Primitives) renderer.ReleasePrimitive(primitive);
    }

    private int Material(UPath path, int slot)
    {
        if (materialFor is not null) return materialFor(path, slot);
        if (_defaultMaterial < 0) _defaultMaterial = renderer.Materials.AddDefaultMaterial(Vector4.One);
        return _defaultMaterial;
    }
}

/// <summary>A material's description and textures as a game's document parser produces them.</summary>
public sealed record PbrMaterialSource(PbrMaterialDesc Material, PbrMaterialTextures Textures);

/// <summary>A material made resident: the id draws bind.</summary>
public sealed record PbrMaterialAsset(int MaterialId);

/// <summary>Loads materials whose KTX2 textures are transcoded on the loader's worker, so the render
/// thread only uploads (<see cref="PbrMaterialTextures.Transcode"/>).</summary>
/// <param name="renderer">The renderer materials are added to.</param>
/// <param name="backend">The backend whose <see cref="IRenderer.SupportedTextureCompression"/> textures are transcoded for.</param>
/// <param name="parse">Turns a material document into its description and KTX2 textures. Runs on the worker,
/// so it may read texture files, but must not touch the renderer.</param>
public sealed class PbrMaterialAssetType(PbrRenderer renderer, IRenderer backend, Func<UPath, byte[], PbrMaterialSource> parse)
    : IAssetType<PbrMaterialSource, PbrMaterialAsset>
{
    private readonly TextureCompressionFormats _supported = backend.SupportedTextureCompression;

    public PbrMaterialSource Decode(UPath path, byte[] bytes)
    {
        var source = parse(path, bytes);
        return source with { Textures = source.Textures.Transcode(_supported) };
    }

    public PbrMaterialAsset Upload(PbrMaterialSource decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        return new PbrMaterialAsset(renderer.Materials.AddMaterial(decoded.Material, decoded.Textures));
    }

    public void Unload(PbrMaterialAsset resident)
    {
        ArgumentNullException.ThrowIfNull(resident);
        renderer.Materials.ReleaseMaterial(resident.MaterialId);
    }
}
