#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace Paradise.Export.NavMesh
{
    /// <summary>
    /// Converts a baked navmesh triangulation (vertices + triangle indices) into a DotRecast
    /// <see cref="DtNavMesh"/> and serializes it as the runtime's <c>MeshSet</c> binary
    /// (<c>data/scenes/&lt;Scene&gt;.navmesh.bin</c>).
    ///
    /// Ported from ParadiseUnityEditor's NavMeshExporter — the quantization/adjacency logic is
    /// engine-neutral and coordinate-agnostic. The contract is right-handed (Godot-native), so inputs
    /// are the baked vertices/winding verbatim, with no handedness mirror (see CONVENTIONS.md). The
    /// same builder is reused at runtime by Paradise.Sample.Game.Navigation.Detour to query the mesh in-memory.
    /// </summary>
    public static class NavMeshBinaryWriter
    {
        private const float CellSize = 0.1f;
        private const float CellHeight = 0.1f;
        private const float AgentHeight = 1.8f;
        private const float AgentRadius = 0.0f;
        private const float AgentMaxClimb = 0.3f;
        private const int VertsPerPoly = 3;
        private const int NullNeighbor = 0xFFFF;
        private const int EdgeCoordinateScale = 1000;

        /// <summary>Build + serialize to <paramref name="outputPath"/>. Returns the byte count written.
        /// <paramref name="warn"/> receives non-fatal diagnostics (e.g. dropped seams).</summary>
        public static long Write(
            string outputPath,
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<int> triangleIndices,
            Action<string>? warn = null)
        {
            DtNavMesh navMesh = BuildNavMesh(vertices, triangleIndices, warn);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            using (var fs = File.Create(outputPath))
            using (var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: false))
            {
                new DtMeshSetWriter().Write(bw, navMesh, RcByteOrder.LITTLE_ENDIAN, false);
            }

            return new FileInfo(outputPath).Length;
        }

        public static DtNavMesh BuildNavMesh(
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<int> indices,
            Action<string>? warn = null)
        {
            if (vertices.Count == 0 || indices.Count == 0)
            {
                throw new InvalidOperationException("NavMesh triangulation is empty.");
            }

            (RcVec3f bmin, RcVec3f bmax) = CalculateBounds(vertices);
            int[] quantizedVertices = QuantizeVertices(vertices, bmin);

            var triangleVertexIndices = new List<int[]>();
            for (int i = 0; i < indices.Count; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];
                ValidateVertexIndex(a, vertices.Count);
                ValidateVertexIndex(b, vertices.Count);
                ValidateVertexIndex(c, vertices.Count);

                if (IsSourceTriangleDegenerate(vertices[a], vertices[b], vertices[c]))
                {
                    continue;
                }

                triangleVertexIndices.Add(new[] { a, b, c });
            }

            if (triangleVertexIndices.Count == 0)
            {
                throw new InvalidOperationException("NavMesh triangulation did not contain any non-degenerate triangles.");
            }

            int polyCount = triangleVertexIndices.Count;
            var polys = new int[polyCount * VertsPerPoly * 2];
            var polyFlags = new int[polyCount];
            var polyAreas = new int[polyCount];

            for (int polyIndex = 0; polyIndex < polyCount; polyIndex++)
            {
                int dst = polyIndex * VertsPerPoly * 2;
                int[] tri = triangleVertexIndices[polyIndex];
                polys[dst] = tri[0];
                polys[dst + 1] = tri[1];
                polys[dst + 2] = tri[2];
                polys[dst + 3] = NullNeighbor;
                polys[dst + 4] = NullNeighbor;
                polys[dst + 5] = NullNeighbor;
                polyFlags[polyIndex] = 1;
                polyAreas[polyIndex] = 0;
            }

            PopulateAdjacency(triangleVertexIndices, vertices, polys, warn);

            var createParams = new DtNavMeshCreateParams
            {
                verts = quantizedVertices,
                vertCount = vertices.Count,
                polys = polys,
                polyAreas = polyAreas,
                polyFlags = polyFlags,
                polyCount = polyCount,
                nvp = VertsPerPoly,
                bmin = bmin,
                bmax = bmax,
                cs = CellSize,
                ch = CellHeight,
                walkableHeight = AgentHeight,
                walkableRadius = AgentRadius,
                walkableClimb = AgentMaxClimb,
                buildBvTree = true,
            };

            DtMeshData? meshData = DtNavMeshBuilder.CreateNavMeshData(createParams);
            if (meshData == null)
            {
                throw new InvalidOperationException("DtNavMeshBuilder.CreateNavMeshData returned null.");
            }

            var navMesh = new DtNavMesh();
            DtStatus status = navMesh.Init(meshData, VertsPerPoly, 0);
            if (status.Failed())
            {
                throw new InvalidOperationException($"DtNavMesh.Init failed with status: {status}");
            }

            return navMesh;
        }

        private static (RcVec3f bmin, RcVec3f bmax) CalculateBounds(IReadOnlyList<Vector3> vertices)
        {
            Vector3 min = vertices[0];
            Vector3 max = vertices[0];
            for (int i = 1; i < vertices.Count; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }

            float yPadding = AgentHeight + 1f;
            var bmin = new RcVec3f(min.X, min.Y - 1f, min.Z);
            var bmax = new RcVec3f(max.X, MathF.Max(max.Y + yPadding, min.Y + yPadding + 1f), max.Z);
            return (bmin, bmax);
        }

        private static int[] QuantizeVertices(IReadOnlyList<Vector3> vertices, RcVec3f bmin)
        {
            var quantized = new int[vertices.Count * 3];
            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3 vertex = vertices[i];
                int dst = i * 3;
                quantized[dst] = Quantize(vertex.X, bmin.X, CellSize);
                quantized[dst + 1] = Quantize(vertex.Y, bmin.Y, CellHeight);
                quantized[dst + 2] = Quantize(vertex.Z, bmin.Z, CellSize);
            }

            return quantized;
        }

        private static void ValidateVertexIndex(int index, int vertexCount)
        {
            if (index < 0 || index >= vertexCount)
            {
                throw new InvalidOperationException(
                    $"NavMesh triangulation referenced vertex index {index}, but only {vertexCount} vertices exist.");
            }
        }

        private static bool IsSourceTriangleDegenerate(Vector3 a, Vector3 b, Vector3 c)
        {
            // float.Epsilon is effectively zero (not a geometric tolerance): only triangles with an
            // exactly-zero doubled-area cross product are dropped. Kept verbatim from the Unity tool
            // intentionally, so both toolchains drop the same triangles.
            Vector3 cross = Vector3.Cross(b - a, c - a);
            return cross.LengthSquared() <= float.Epsilon;
        }

        private static int Quantize(float value, float origin, float cellSize) =>
            Math.Max(0, (int)Math.Round((value - origin) / cellSize));

        private static void PopulateAdjacency(
            List<int[]> triangleVertexIndices,
            IReadOnlyList<Vector3> vertices,
            int[] polys,
            Action<string>? warn)
        {
            var edges = new Dictionary<EdgeKey, EdgeRef>();
            for (int polyIndex = 0; polyIndex < triangleVertexIndices.Count; polyIndex++)
            {
                int[] tri = triangleVertexIndices[polyIndex];
                for (int edgeIndex = 0; edgeIndex < VertsPerPoly; edgeIndex++)
                {
                    int start = tri[edgeIndex];
                    int end = tri[(edgeIndex + 1) % VertsPerPoly];
                    var key = new EdgeKey(Math.Min(start, end), Math.Max(start, end));

                    if (edges.TryGetValue(key, out EdgeRef other))
                    {
                        SetNeighbor(polys, polyIndex, edgeIndex, other.PolyIndex);
                        SetNeighbor(polys, other.PolyIndex, other.EdgeIndex, polyIndex);
                        edges.Remove(key);
                        continue;
                    }

                    edges.Add(key, new EdgeRef(polyIndex, edgeIndex));
                }
            }

            ConnectDuplicateWorldEdges(edges.Values, triangleVertexIndices, vertices, polys, warn);
        }

        private static void ConnectDuplicateWorldEdges(
            IEnumerable<EdgeRef> unmatchedEdges,
            List<int[]> triangleVertexIndices,
            IReadOnlyList<Vector3> vertices,
            int[] polys,
            Action<string>? warn)
        {
            var worldEdges = new Dictionary<WorldEdgeKey, List<EdgeRef>>();
            foreach (EdgeRef edge in unmatchedEdges)
            {
                int[] tri = triangleVertexIndices[edge.PolyIndex];
                int start = tri[edge.EdgeIndex];
                int end = tri[(edge.EdgeIndex + 1) % VertsPerPoly];
                var key = new WorldEdgeKey(vertices[start], vertices[end]);

                if (!worldEdges.TryGetValue(key, out List<EdgeRef>? matchingEdges))
                {
                    worldEdges.Add(key, matchingEdges = new List<EdgeRef>());
                }

                matchingEdges.Add(edge);
            }

            foreach (List<EdgeRef> matchingEdges in worldEdges.Values)
            {
                if (matchingEdges.Count != 2)
                {
                    // >2 polygons on one world edge (e.g. a T-junction): leaving these unmatched
                    // creates a silent connectivity hole, so surface it rather than dropping quietly.
                    if (matchingEdges.Count > 2)
                    {
                        warn?.Invoke(
                            $"Skipped ambiguous navmesh boundary edge shared by {matchingEdges.Count} polygons " +
                            "(possible T-junction; agents may not traverse this seam).");
                    }

                    continue;
                }

                EdgeRef first = matchingEdges[0];
                EdgeRef second = matchingEdges[1];
                SetNeighbor(polys, first.PolyIndex, first.EdgeIndex, second.PolyIndex);
                SetNeighbor(polys, second.PolyIndex, second.EdgeIndex, first.PolyIndex);
            }
        }

        private static void SetNeighbor(int[] polys, int polyIndex, int edgeIndex, int neighborPolyIndex)
        {
            int baseIndex = polyIndex * VertsPerPoly * 2 + VertsPerPoly;
            polys[baseIndex + edgeIndex] = neighborPolyIndex;
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            public EdgeKey(int a, int b)
            {
                A = a;
                B = b;
            }

            public int A { get; }
            public int B { get; }

            public bool Equals(EdgeKey other) => A == other.A && B == other.B;
            public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(A, B);
        }

        private readonly struct EdgeRef
        {
            public EdgeRef(int polyIndex, int edgeIndex)
            {
                PolyIndex = polyIndex;
                EdgeIndex = edgeIndex;
            }

            public int PolyIndex { get; }
            public int EdgeIndex { get; }
        }

        private readonly struct WorldVertexKey : IComparable<WorldVertexKey>, IEquatable<WorldVertexKey>
        {
            public WorldVertexKey(Vector3 vertex)
            {
                X = QuantizeEdgeCoordinate(vertex.X);
                Y = QuantizeEdgeCoordinate(vertex.Y);
                Z = QuantizeEdgeCoordinate(vertex.Z);
            }

            public int X { get; }
            public int Y { get; }
            public int Z { get; }

            public int CompareTo(WorldVertexKey other)
            {
                int x = X.CompareTo(other.X);
                if (x != 0)
                {
                    return x;
                }

                int y = Y.CompareTo(other.Y);
                return y != 0 ? y : Z.CompareTo(other.Z);
            }

            public bool Equals(WorldVertexKey other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object? obj) => obj is WorldVertexKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(X, Y, Z);

            private static int QuantizeEdgeCoordinate(float value) =>
                (int)MathF.Round(value * EdgeCoordinateScale);
        }

        private readonly struct WorldEdgeKey : IEquatable<WorldEdgeKey>
        {
            public WorldEdgeKey(Vector3 a, Vector3 b)
            {
                var first = new WorldVertexKey(a);
                var second = new WorldVertexKey(b);
                if (first.CompareTo(second) <= 0)
                {
                    A = first;
                    B = second;
                }
                else
                {
                    A = second;
                    B = first;
                }
            }

            public WorldVertexKey A { get; }
            public WorldVertexKey B { get; }

            public bool Equals(WorldEdgeKey other) => A.Equals(other.A) && B.Equals(other.B);
            public override bool Equals(object? obj) => obj is WorldEdgeKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(A, B);
        }
    }
}
