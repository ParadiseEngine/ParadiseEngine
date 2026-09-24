using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace Paradise.Export.NavMesh;

/// <summary>Bakes collision triangles with Recast and previews the resulting Detour surface.</summary>
public static class NavMeshBakeService
{
    private const int MaxHeightfieldCells = 16 * 1024 * 1024;
    private const int MaxVerticesPerPolygon = 6;

    public static NavMeshBakeInput ReadInput(string json) =>
        JsonSerializer.Deserialize(json, NavMeshBakeJsonContext.Default.NavMeshBakeInput)
        ?? throw new InvalidDataException("Navigation geometry must be a JSON object.");

    public static string WritePreview(NavMeshPreview preview) =>
        JsonSerializer.Serialize(preview, NavMeshBakeJsonContext.Default.NavMeshPreview);

    /// <summary>Rasterizes, filters and erodes the geometry before constructing navigation polygons.</summary>
    public static NavMeshBakeResult Bake(NavMeshBakeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        float[] vertices = Validate(input);
        var settings = input.Settings;
        var geometry = new RcSampleInputGeomProvider(vertices, input.Indices);
        var minimum = geometry.GetMeshBoundsMin();
        var maximum = geometry.GetMeshBoundsMax();
        ValidateBounds(minimum, maximum, settings);
        minimum.Y -= settings.CellHeight;
        maximum.Y += settings.AgentHeight + settings.CellHeight;

        var config = new RcConfig(
            RcPartition.WATERSHED, settings.CellSize, settings.CellHeight,
            settings.MaxSlope, settings.AgentHeight, settings.AgentRadius, settings.MaxClimb,
            regionMinSize: 0, regionMergeSize: 0, edgeMaxLen: 12f, edgeMaxError: 1.3f,
            vertsPerPoly: MaxVerticesPerPolygon, detailSampleDist: 6f, detailSampleMaxError: 1f,
            filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
            walkableAreaMod: new RcAreaModification(1), buildMeshDetail: true);
        var built = new RcBuilder().Build(geometry, new RcBuilderConfig(config, minimum, maximum), keepInterResults: false);
        var polygons = built.Mesh;
        if (polygons.npolys == 0)
        {
            throw new InvalidDataException("Navigation bake produced no walkable polygons; check face winding, geometry and agent settings.");
        }

        Array.Fill(polygons.flags, 1);
        var detail = built.MeshDetail;
        var options = new DtNavMeshCreateParams
        {
            verts = polygons.verts,
            vertCount = polygons.nverts,
            polys = polygons.polys,
            polyAreas = polygons.areas,
            polyFlags = polygons.flags,
            polyCount = polygons.npolys,
            nvp = polygons.nvp,
            detailMeshes = detail.meshes,
            detailVerts = detail.verts,
            detailVertsCount = detail.nverts,
            detailTris = detail.tris,
            detailTriCount = detail.ntris,
            walkableHeight = settings.AgentHeight,
            walkableRadius = settings.AgentRadius,
            walkableClimb = settings.MaxClimb,
            bmin = polygons.bmin,
            bmax = polygons.bmax,
            cs = settings.CellSize,
            ch = settings.CellHeight,
            buildBvTree = true,
        };
        var data = DtNavMeshBuilder.CreateNavMeshData(options)
            ?? throw new InvalidDataException("Navigation bake exceeds the Detour single-tile mesh limits.");
        var mesh = new DtNavMesh();
        if (mesh.Init(data, polygons.nvp, 0).Failed())
        {
            throw new InvalidDataException("The baked navigation mesh could not be initialized.");
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            new DtMeshSetWriter().Write(writer, mesh, RcByteOrder.LITTLE_ENDIAN, cCompatibility: false);
        }

        return new NavMeshBakeResult(stream.ToArray(), Preview(mesh));
    }

    /// <summary>Reads existing Recast4J MeshSet bytes without rebaking their source geometry.</summary>
    public static NavMeshPreview Preview(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream);
            return Preview(new DtMeshSetReader().Read(reader));
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or ArgumentException
            or IndexOutOfRangeException or OverflowException or NullReferenceException)
        {
            throw new InvalidDataException($"Invalid baked Detour navmesh: {failure.Message}", failure);
        }
    }

    private static NavMeshPreview Preview(DtNavMesh mesh)
    {
        var vertices = new List<float[]>();
        var indices = new List<int>();
        for (int tileIndex = 0; tileIndex < mesh.GetMaxTiles(); tileIndex++)
        {
            var data = mesh.GetTile(tileIndex)?.data;
            if (data?.header is null) continue;
            for (int polygonIndex = 0; polygonIndex < data.header.polyCount; polygonIndex++)
            {
                var polygon = data.polys[polygonIndex];
                if (polygon.flags == 0 || polygon.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION) continue;
                if (data.detailMeshes is { Length: > 0 })
                {
                    var detail = data.detailMeshes[polygonIndex];
                    for (int triangle = 0; triangle < detail.triCount; triangle++)
                    {
                        for (int corner = 0; corner < 3; corner++)
                        {
                            int vertex = data.detailTris[(detail.triBase + triangle) * 4 + corner];
                            bool onPolygon = vertex < polygon.vertCount;
                            AddVertex(onPolygon ? data.verts : data.detailVerts,
                                onPolygon ? polygon.verts[vertex] : detail.vertBase + vertex - polygon.vertCount,
                                vertices, indices);
                        }
                    }
                }
                else
                {
                    for (int corner = 2; corner < polygon.vertCount; corner++)
                    {
                        AddVertex(data.verts, polygon.verts[0], vertices, indices);
                        AddVertex(data.verts, polygon.verts[corner - 1], vertices, indices);
                        AddVertex(data.verts, polygon.verts[corner], vertices, indices);
                    }
                }
            }
        }

        if (indices.Count == 0) throw new InvalidDataException("The mesh contains no walkable polygons.");
        return new NavMeshPreview { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
    }

    private static void AddVertex(float[] source, int index, List<float[]> vertices, List<int> indices)
    {
        int offset = checked(index * 3);
        float x = source[offset];
        float y = source[offset + 1];
        float z = source[offset + 2];
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new InvalidDataException("Navigation polygon coordinates must be finite.");
        }

        indices.Add(vertices.Count);
        vertices.Add([x, y, z]);
    }

    private static float[] Validate(NavMeshBakeInput input)
    {
        if (input.Vertices is null || input.Vertices.Length < 3)
            throw new InvalidDataException("Navigation geometry needs at least three vertices.");
        if (input.Indices is null || input.Indices.Length == 0 || input.Indices.Length % 3 != 0)
            throw new InvalidDataException("Navigation indices must contain complete triangles.");
        if (input.Settings is not { } settings)
            throw new InvalidDataException("Navigation settings must be an object.");

        Positive(settings.CellSize, "cellSize");
        Positive(settings.CellHeight, "cellHeight");
        Positive(settings.AgentHeight, "agentHeight");
        Nonnegative(settings.AgentRadius, "agentRadius");
        Nonnegative(settings.MaxClimb, "maxClimb");
        Nonnegative(settings.MaxSlope, "maxSlope");
        if (settings.MaxSlope >= 90f) throw new InvalidDataException("maxSlope must be less than 90 degrees.");
        if (settings.AgentHeight / settings.CellHeight < 3f)
            throw new InvalidDataException("agentHeight must span at least three cellHeight voxels.");
        if (settings.MaxClimb >= settings.AgentHeight)
            throw new InvalidDataException("maxClimb must be less than agentHeight.");

        var vertices = new float[checked(input.Vertices.Length * 3)];
        for (int index = 0; index < input.Vertices.Length; index++)
        {
            var vertex = input.Vertices[index];
            if (vertex is null || vertex.Length != 3)
                throw new InvalidDataException($"vertices[{index}] must contain exactly three coordinates.");
            for (int axis = 0; axis < 3; axis++)
            {
                if (!float.IsFinite(vertex[axis]))
                    throw new InvalidDataException($"vertices[{index}] must contain finite coordinates.");
                vertices[index * 3 + axis] = vertex[axis];
            }
        }

        foreach (int index in input.Indices)
        {
            if ((uint)index >= input.Vertices.Length)
                throw new InvalidDataException($"Navigation triangle index {index} is outside the vertex array.");
        }

        return vertices;
    }

    private static void ValidateBounds(RcVec3f minimum, RcVec3f maximum, NavMeshBakeSettings settings)
    {
        double width = Math.Ceiling(((double)maximum.X - minimum.X) / settings.CellSize);
        double depth = Math.Ceiling(((double)maximum.Z - minimum.Z) / settings.CellSize);
        double height = ((double)maximum.Y - minimum.Y + settings.AgentHeight) / settings.CellHeight + 2;
        if (width < 1 || depth < 1)
            throw new InvalidDataException("Navigation geometry must cover an area in X and Z.");
        if (width * depth > MaxHeightfieldCells || height > 8191
            || settings.AgentRadius / settings.CellSize > 255)
        {
            throw new InvalidDataException("Navigation geometry exceeds the voxel bake limits; reduce its bounds or increase cell size/height.");
        }
    }

    private static void Positive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new InvalidDataException($"{name} must be finite and greater than zero.");
    }

    private static void Nonnegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new InvalidDataException($"{name} must be finite and nonnegative.");
    }
}
