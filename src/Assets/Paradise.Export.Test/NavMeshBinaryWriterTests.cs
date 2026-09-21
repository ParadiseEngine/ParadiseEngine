using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using DotRecast.Detour;
using Paradise.Export.NavMesh;

namespace Paradise.Export.Tests;

// Functional test of the engine-neutral navmesh binary writer: a flat quad (two triangles) must
// build a valid DtNavMesh and serialize to a non-empty MeshSet file. Exercises quantization,
// adjacency, DotRecast createParams, and DtMeshSetWriter end to end — no Godot needed.
public class NavMeshBinaryWriterTests
{
    // A 2x2 quad on the XZ plane (y=0), two triangles, right-handed (Godot-native) coordinates.
    private static (List<Vector3> verts, List<int> tris) Quad()
    {
        var verts = new List<Vector3>
        {
            new(0f, 0f, 0f),
            new(2f, 0f, 0f),
            new(2f, 0f, 2f),
            new(0f, 0f, 2f),
        };
        var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
        return (verts, tris);
    }

    [Test]
    public async Task build_produces_navmesh_with_a_tile()
    {
        (List<Vector3> verts, List<int> tris) = Quad();
        DtNavMesh navMesh = NavMeshBinaryWriter.BuildNavMesh(verts, tris);

        await Assert.That(navMesh).IsNotNull();
        await Assert.That(navMesh.GetMaxTiles()).IsGreaterThanOrEqualTo(1);
        await Assert.That(navMesh.GetTile(0)).IsNotNull();
    }

    [Test]
    public async Task write_produces_non_empty_binary()
    {
        (List<Vector3> verts, List<int> tris) = Quad();
        string path = Path.Combine(Path.GetTempPath(), $"paradise_nav_{Guid.NewGuid():N}.navmesh.bin");
        try
        {
            long bytes = NavMeshBinaryWriter.Write(path, verts, tris);
            await Assert.That(bytes).IsGreaterThan(0L);
            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Test]
    public async Task shared_edge_produces_adjacency()
    {
        // The quad's two triangles share an interior edge; PopulateAdjacency must connect them,
        // so at least one polygon neighbour slot is set (non-zero) rather than all NullNeighbor.
        (List<Vector3> verts, List<int> tris) = Quad();
        DtMeshData data = NavMeshBinaryWriter.BuildNavMesh(verts, tris).GetTile(0).data;

        bool hasNeighbour = false;
        for (int p = 0; p < data.header.polyCount && !hasNeighbour; p++)
        {
            DtPoly poly = data.polys[p];
            for (int e = 0; e < poly.vertCount; e++)
            {
                if (poly.neis[e] != 0)
                {
                    hasNeighbour = true;
                    break;
                }
            }
        }

        await Assert.That(hasNeighbour).IsTrue();
    }

    [Test]
    public async Task empty_triangulation_throws()
    {
        await Assert.That(() => NavMeshBinaryWriter.BuildNavMesh(new List<Vector3>(), new List<int>()))
            .Throws<InvalidOperationException>();
    }
}
