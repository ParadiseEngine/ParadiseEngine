using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using Paradise.Export.NavMesh;

namespace Paradise.Export.Tests;

public class NavMeshBakeTests
{
    [Test]
    public async Task baking_erodes_the_floor_by_agent_radius_and_serializes_a_queryable_mesh()
    {
        var input = Floor();
        input.Settings.AgentRadius = 0.6f;
        var baked = NavMeshBakeService.Bake(input);
        using var stream = new MemoryStream(baked.Bytes);
        using var reader = new BinaryReader(stream);
        var mesh = new DtMeshSetReader().Read(reader);
        var query = new DtNavMeshQuery(mesh);
        var status = query.FindNearestPoly(new RcVec3f(5, 0, 5), new RcVec3f(0.1f, 0.3f, 0.1f),
            new DtQueryDefaultFilter(), out var polygon, out _, out _);

        await Assert.That(status.Failed()).IsFalse();
        await Assert.That(polygon).IsNotEqualTo(0L);
        await Assert.That(mesh.GetTile(0).data.header.walkableRadius).IsEqualTo(0.6f);
        await Assert.That(baked.Preview.Vertices.Min(vertex => vertex[0])).IsGreaterThanOrEqualTo(0.6f);
        await Assert.That(baked.Preview.Vertices.Max(vertex => vertex[0])).IsLessThanOrEqualTo(9.4f);
        await Assert.That(NavMeshBakeService.WritePreview(NavMeshBakeService.Preview(baked.Bytes)))
            .IsEqualTo(NavMeshBakeService.WritePreview(baked.Preview));
    }

    [Test]
    public async Task low_ceiling_removes_the_floor_for_a_tall_agent()
    {
        var input = Floor();
        input.Vertices = [.. input.Vertices, [0, 1, 0], [0, 1, 10], [10, 1, 10], [10, 1, 0]];
        input.Indices = [.. input.Indices, 4, 5, 6, 4, 6, 7];
        var baked = NavMeshBakeService.Bake(input);
        using var stream = new MemoryStream(baked.Bytes);
        using var reader = new BinaryReader(stream);
        var mesh = new DtMeshSetReader().Read(reader);
        var query = new DtNavMeshQuery(mesh);
        query.FindNearestPoly(new RcVec3f(5, 0, 5), new RcVec3f(0.1f, 0.3f, 0.1f),
            new DtQueryDefaultFilter(), out var floor, out _, out _);
        query.FindNearestPoly(new RcVec3f(5, 1, 5), new RcVec3f(0.1f, 0.3f, 0.1f),
            new DtQueryDefaultFilter(), out var roof, out _, out _);

        await Assert.That(floor).IsEqualTo(0L);
        await Assert.That(roof).IsNotEqualTo(0L);
    }

    [Test]
    public async Task baked_obstacle_preserves_a_connected_route_around_it()
    {
        var input = Floor();
        input.Vertices = [.. input.Vertices,
            [4, 0, 4], [4, 0, 6], [6, 0, 6], [6, 0, 4],
            [4, 3, 4], [4, 3, 6], [6, 3, 6], [6, 3, 4]];
        input.Indices = [.. input.Indices,
            4, 6, 5, 4, 7, 6, 8, 9, 10, 8, 10, 11,
            4, 5, 9, 4, 9, 8, 5, 6, 10, 5, 10, 9,
            6, 7, 11, 6, 11, 10, 7, 4, 8, 7, 8, 11];
        using var stream = new MemoryStream(NavMeshBakeService.Bake(input).Bytes);
        using var reader = new BinaryReader(stream);
        var mesh = new DtMeshSetReader().Read(reader);
        var query = new DtNavMeshQuery(mesh);
        var filter = new DtQueryDefaultFilter();
        var extents = new RcVec3f(0.1f, 0.3f, 0.1f);
        query.FindNearestPoly(new RcVec3f(2, 0, 5), extents, filter, out var startRef, out var start, out _);
        query.FindNearestPoly(new RcVec3f(8, 0, 5), extents, filter, out var endRef, out var end, out _);
        var corridor = new long[64];
        var rayStatus = query.Raycast(startRef, start, end, filter, out var fraction, out _,
            corridor, out _, corridor.Length);
        var status = query.FindPath(startRef, endRef, start, end, filter, corridor, out var count, corridor.Length);

        await Assert.That(startRef).IsNotEqualTo(0L);
        await Assert.That(endRef).IsNotEqualTo(0L);
        await Assert.That(rayStatus.Succeeded()).IsTrue();
        await Assert.That(fraction).IsGreaterThan(0f).And.IsLessThan(1f);
        await Assert.That(status.Succeeded()).IsTrue();
        await Assert.That(status.Has(DtStatus.DT_PARTIAL_RESULT | DtStatus.DT_BUFFER_TOO_SMALL | DtStatus.DT_OUT_OF_NODES)).IsFalse();
        await Assert.That(count).IsGreaterThan(1);
        await Assert.That(corridor[count - 1]).IsEqualTo(endRef);
    }

    [Test]
    public async Task slopes_above_the_limit_are_not_copied_as_walkable_triangles()
    {
        var input = Floor();
        input.Vertices[2][1] = 20f;
        input.Vertices[3][1] = 20f;
        await Assert.That(() => NavMeshBakeService.Bake(input))
            .Throws<InvalidDataException>().WithMessageContaining("no walkable polygons");
    }

    [Test]
    [Arguments("indices")]
    [Arguments("vertex")]
    [Arguments("cellSize")]
    [Arguments("bounds")]
    public async Task malformed_or_unbounded_geometry_is_rejected_before_baking(string problem)
    {
        var input = Floor();
        switch (problem)
        {
            case "indices": input.Indices[0] = 100; break;
            case "vertex": input.Vertices[0][0] = float.NaN; break;
            case "cellSize": input.Settings.CellSize = 0f; break;
            case "bounds": input.Vertices[2][0] = float.MaxValue; break;
        }

        await Assert.That(() => NavMeshBakeService.Bake(input)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task geometry_json_uses_defaults_and_rejects_misspelled_settings()
    {
        var input = NavMeshBakeService.ReadInput("""
            {"vertices":[[0,0,0],[0,0,10],[10,0,10],[10,0,0]],"indices":[0,1,2,0,2,3]}
            """);

        await Assert.That(input.Settings.AgentRadius).IsEqualTo(0.35f);
        await Assert.That(NavMeshBakeService.Bake(input).Preview.Indices.Length).IsGreaterThan(0);
        await Assert.That(() => NavMeshBakeService.ReadInput("""{"settings":{"agentRaduis":0.5}}"""))
            .Throws<JsonException>();
    }

    [Test]
    public async Task preview_reads_existing_binary_without_requiring_bake_geometry()
    {
        var mesh = NavMeshBinaryWriter.BuildNavMesh(
            [Vector3.Zero, new Vector3(0, 0, 2), new Vector3(2, 0, 2), new Vector3(2, 0, 0)],
            [0, 1, 2, 0, 2, 3]);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            new DtMeshSetWriter().Write(writer, mesh, DotRecast.Core.RcByteOrder.LITTLE_ENDIAN, false);
        }

        var preview = NavMeshBakeService.Preview(stream.ToArray());
        await Assert.That(preview.Indices.Length).IsEqualTo(6);
        await Assert.That(preview.Vertices.Min(vertex => vertex[0])).IsEqualTo(0f);
        await Assert.That(preview.Vertices.Max(vertex => vertex[0])).IsEqualTo(2f);
        await Assert.That(() => NavMeshBakeService.Preview([0, 1, 2, 3]))
            .Throws<InvalidDataException>().WithMessageContaining("Invalid baked Detour navmesh");
    }

    private static NavMeshBakeInput Floor() => new()
    {
        Vertices = [[0, 0, 0], [0, 0, 10], [10, 0, 10], [10, 0, 0]],
        Indices = [0, 1, 2, 0, 2, 3],
    };
}
