using System.Numerics;
using System.Text;

using Paradise.BLOB;

namespace Paradise.Assets.Mesh;

/// <summary>Which interleaved vertex stream the blob carries; the stride follows from it.</summary>
public enum MeshVertexLayout : byte
{
    /// <summary>pos3 normal3 uv2 tangent4 — 12 floats, the renderer's static layout.</summary>
    Static = 0,

    /// <summary>The static 12 followed by joints4 (indices as floats) and weights4 — 20 floats, the renderer's skinned layout.</summary>
    Skinned = 1,
}

/// <summary>One draw inside the blob: a run of indices, the material slot it binds, and where it sits in the skeleton when skinned.</summary>
/// <remarks>
/// <c>MaterialSlot</c> <c>i</c> is glTF primitive <c>i</c> — the material-slot contract, frozen
/// here. <c>NodeIndex</c> is the skeleton node the draw's mesh sat on, or −1: a static draw has
/// its node transform baked into its vertices and needs none. <c>SkinIndex</c> names the skin
/// whose palette skins the draw, or −1 for a rigid one. <c>Name</c> is the glTF node's, so a game
/// can still address a draw by the name its authors know.
/// </remarks>
public struct MeshDraw
{
    public uint FirstIndex;
    public uint IndexCount;
    public int MaterialSlot;
    public int NodeIndex;
    public int SkinIndex;
    public BlobString<UTF8Encoding> Name;

    public readonly bool IsSkinned => SkinIndex >= 0;
}

/// <summary>
/// The Paradise mesh blob root: what a GLB's default scene becomes once the pipeline has baked
/// its node graph into draws. Rigid draws carry their world transform in their vertices, so one
/// model matrix per entity is the whole placement; skinned draws stay in bind space and name the
/// skin and node the runtime's palette is computed for.
/// </summary>
/// <remarks>
/// A <c>Paradise.BLOB</c> layout, like a collision world or a behaviour tree: written once by the
/// pipeline (<c>paradise assets extract</c>), read by the runtime from one aligned native copy —
/// no parse. The magic and version are the first two fields so a reader refuses a foreign or
/// newer blob before touching an offset. Deterministic for a given source, so the blob lives in
/// the asset tree beside the GLB it came from. Reach every <c>BlobArray</c>/<c>BlobString</c>
/// member through a mutable <c>ref</c>: a readonly reference copies the header and the copy's
/// relative offset points nowhere.
/// </remarks>
public struct MeshBlob
{
    public const uint ExpectedMagic = 0x48534D50;   // "PMSH"

    public const uint ExpectedVersion = 1;

    public const int StaticFloatsPerVertex = 12;

    public const int SkinnedFloatsPerVertex = 20;

    public uint Magic;
    public uint Version;
    public MeshVertexLayout Layout;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public BlobArray<float> Vertices;
    public BlobArray<uint> Indices;
    public BlobArray<MeshDraw> Draws;

    public readonly int FloatsPerVertex => Layout == MeshVertexLayout.Skinned ? SkinnedFloatsPerVertex : StaticFloatsPerVertex;

    // Not `readonly`: a BlobArray member reached through a readonly reference is a defensive
    // COPY of the array header, and its relative offset then points at the stack, not the blob.
    public int VertexCount => Vertices.Length / FloatsPerVertex;

    public readonly bool IsSkinned => Layout == MeshVertexLayout.Skinned;
}

/// <summary>The managed shape the pipeline builds a blob from, and a test reads one back into.</summary>
/// <param name="Draws">Each as (first index, index count, material slot, node index, skin index, name).</param>
public sealed record MeshData(
    MeshVertexLayout Layout,
    float[] Vertices,
    uint[] Indices,
    IReadOnlyList<MeshDrawData> Draws,
    Vector3 BoundsMin,
    Vector3 BoundsMax)
{
    public int FloatsPerVertex => Layout == MeshVertexLayout.Skinned ? MeshBlob.SkinnedFloatsPerVertex : MeshBlob.StaticFloatsPerVertex;

    public int VertexCount => Vertices.Length / FloatsPerVertex;
}

public readonly record struct MeshDrawData(uint FirstIndex, uint IndexCount, int MaterialSlot, int NodeIndex, int SkinIndex, string? Name);

/// <summary>Builds and opens mesh blobs. Bytes in, bytes out; the layout itself is <see cref="MeshBlob"/>.</summary>
public static class MeshBlobFormat
{
    public static byte[] Write(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Vertices.Length % mesh.FloatsPerVertex != 0)
        {
            throw new ArgumentException($"{mesh.Vertices.Length} floats is not a whole number of {mesh.FloatsPerVertex}-float vertices.", nameof(mesh));
        }

        var builder = new StructBuilder<MeshBlob>();
        builder.Value.Magic = MeshBlob.ExpectedMagic;
        builder.Value.Version = MeshBlob.ExpectedVersion;
        builder.Value.Layout = mesh.Layout;
        builder.Value.BoundsMin = mesh.BoundsMin;
        builder.Value.BoundsMax = mesh.BoundsMax;
        builder.SetArray(ref builder.Value.Vertices, mesh.Vertices);
        builder.SetArray(ref builder.Value.Indices, mesh.Indices);
        builder.SetArray(ref builder.Value.Draws, mesh.Draws.Select(Draw));
        return builder.CreateBlob();
    }

    /// <summary>Whether the bytes begin as a mesh blob this build reads.</summary>
    public static bool IsMeshBlob(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 8 && BitConverter.ToUInt32(bytes) == MeshBlob.ExpectedMagic;

    /// <summary>Copies the bytes into aligned native memory and hands back the root; dispose the reference when the geometry is uploaded.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a mesh blob this build reads.</exception>
    public static NativeBlobAssetReference<MeshBlob> Open(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || BitConverter.ToUInt32(bytes) != MeshBlob.ExpectedMagic) throw new InvalidDataException("Not a Paradise mesh blob (bad magic).");
        var reference = new NativeBlobAssetReference<MeshBlob>(bytes);
        try
        {
            Check(ref reference.Value);
        }
        catch
        {
            reference.Dispose();
            throw;
        }

        return reference;
    }

    /// <summary>The blob back as managed data — for tests and tools, not the hot path.</summary>
    public static MeshData Read(byte[] bytes)
    {
        using var reference = Open(bytes);
        ref var blob = ref reference.Value;
        var draws = new MeshDrawData[blob.Draws.Length];
        for (var i = 0; i < draws.Length; i++)
        {
            ref var draw = ref blob.Draws[i];
            draws[i] = new MeshDrawData(draw.FirstIndex, draw.IndexCount, draw.MaterialSlot, draw.NodeIndex, draw.SkinIndex, draw.Name.Length == 0 ? null : draw.Name.ToString());
        }

        return new MeshData(blob.Layout, blob.Vertices.ToArray(), blob.Indices.ToArray(), draws, blob.BoundsMin, blob.BoundsMax);
    }

    private static void Check(ref MeshBlob blob)
    {
        if (blob.Version != MeshBlob.ExpectedVersion) throw new InvalidDataException($"Mesh blob version {blob.Version} is not readable by this build (supports {MeshBlob.ExpectedVersion}).");
        if (blob.Layout is not (MeshVertexLayout.Static or MeshVertexLayout.Skinned)) throw new InvalidDataException($"Unknown vertex layout {(int)blob.Layout}.");
        if (blob.Vertices.Length % blob.FloatsPerVertex != 0) throw new InvalidDataException("Vertex floats are not a whole number of vertices.");
        for (var i = 0; i < blob.Draws.Length; i++)
        {
            ref var draw = ref blob.Draws[i];
            if (draw.FirstIndex + (ulong)draw.IndexCount > (ulong)blob.Indices.Length) throw new InvalidDataException($"Draw {i} runs past the index buffer.");
        }

        // A draw that fits the index buffer can still name a vertex past the end; that is an
        // out-of-range GPU read at upload, so it is refused here where it can be named.
        var vertexCount = (uint)blob.VertexCount;
        for (var i = 0; i < blob.Indices.Length; i++)
        {
            if (blob.Indices[i] >= vertexCount) throw new InvalidDataException($"Index {i} names vertex {blob.Indices[i]} of {vertexCount}.");
        }
    }

    private static IBuilder<MeshDraw> Draw(MeshDrawData data)
    {
        var builder = new StructBuilder<MeshDraw>();
        builder.Value.FirstIndex = data.FirstIndex;
        builder.Value.IndexCount = data.IndexCount;
        builder.Value.MaterialSlot = data.MaterialSlot;
        builder.Value.NodeIndex = data.NodeIndex;
        builder.Value.SkinIndex = data.SkinIndex;
        builder.SetString(ref builder.Value.Name, data.Name ?? string.Empty);
        return builder;
    }
}
