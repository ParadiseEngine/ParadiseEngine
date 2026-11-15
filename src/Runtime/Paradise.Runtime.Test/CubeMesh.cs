using System.Numerics;
using Paradise.Assets.Mesh;

namespace Paradise.Runtime.Test;

/// <summary>A unit cube as the pipeline would cook it: a static mesh blob with one draw.</summary>
internal static class CubeMesh
{
    public static byte[] Cooked() => MeshBlobFormat.Write(Data());

    public static MeshData Data()
    {
        var vertices = new List<float>();
        var indices = new List<uint>();
        Face(Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitX, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitY);
        Face(-Vector3.UnitZ, Vector3.UnitY);
        return new MeshData(
            MeshVertexLayout.Static, [.. vertices], [.. indices],
            [new MeshDrawData(0, (uint)indices.Count, 0, -1, -1, "cube")],
            new Vector3(-0.5f), new Vector3(0.5f));

        void Face(Vector3 normal, Vector3 up)
        {
            var right = Vector3.Cross(up, normal);
            var start = (uint)(vertices.Count / 12);
            ReadOnlySpan<(float U, float V)> corners = [(-0.5f, -0.5f), (0.5f, -0.5f), (0.5f, 0.5f), (-0.5f, 0.5f)];
            foreach (var (u, v) in corners)
            {
                var p = normal * 0.5f + right * u + up * v;
                vertices.AddRange([p.X, p.Y, p.Z, normal.X, normal.Y, normal.Z, u + 0.5f, 0.5f - v, right.X, right.Y, right.Z, 1f]);
            }

            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
    }
}
