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
public sealed record PbrMeshAsset(UPath Path, PbrMesh Mesh, MeshSkinData? Skin);

/// <summary>Loads cooked mesh blobs (<see cref="MeshBlobFormat"/>) into <see cref="PbrMeshAsset"/>s.</summary>
/// <remarks>Decoding the blob into managed arrays runs on the loader's worker; the upload makes one
/// primitive per draw on the render thread. Each draw binds the material
/// <paramref name="materialFor"/> picks for its slot, or a white default.</remarks>
/// <param name="renderer">The renderer primitives are uploaded to; the loader's thread must be its render thread.</param>
/// <param name="materialFor">Maps (mesh path, draw material slot) to a material id; null uses the default.</param>
public sealed class PbrMeshAssetType(PbrRenderer renderer, Func<UPath, int, int>? materialFor = null)
    : IAssetType<PbrMeshAssetType.Cooked, PbrMeshAsset>
{
    private int _defaultMaterial = -1;

    /// <summary>A decoded blob, waiting for its upload.</summary>
    public sealed record Cooked(UPath Path, MeshData Data);

    public Cooked Decode(UPath path, byte[] bytes) => new(path, MeshBlobFormat.Read(bytes));

    public PbrMeshAsset Upload(Cooked decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        var data = decoded.Data;
        var primitives = new PbrPrimitive[data.Draws.Count];
        var uploaded = 0;
        try
        {
            for (; uploaded < primitives.Length; uploaded++)
            {
                var draw = data.Draws[uploaded];
                var indices = data.Indices.AsSpan((int)draw.FirstIndex, (int)draw.IndexCount);
                var material = Material(decoded.Path, draw.MaterialSlot);
                primitives[uploaded] = data.Layout == MeshVertexLayout.Skinned && draw.SkinIndex >= 0
                    ? renderer.UploadSkinnedPrimitive(data.Vertices, indices, material)
                    : renderer.UploadPrimitive(data.Vertices, indices, material, stride: data.FloatsPerVertex);
            }
        }
        catch
        {
            for (var i = 0; i < uploaded; i++) renderer.ReleasePrimitive(primitives[i]);
            throw;
        }

        return new PbrMeshAsset(decoded.Path, new PbrMesh(primitives), data.Skin);
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
