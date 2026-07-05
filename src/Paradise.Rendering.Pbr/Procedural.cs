using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Procedural geometry in the renderer's interleaved vertex layout (12 floats:
/// pos3/normal3/uv2/tangent4) — for samples, tests, and placeholder visuals.</summary>
public static class Procedural
{
    /// <summary>A unit cube centered on the origin: 24 vertices (per-face normals/tangents),
    /// 36 indices, CCW front faces for the RH −Z-forward convention.</summary>
    public static (float[] Vertices, uint[] Indices) UnitCube()
    {
        var faces = new (Vector3 Normal, Vector3 U, Vector3 V)[]
        {
            (new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)),   // +Z
            (new(0, 0, -1), new(-1, 0, 0), new(0, 1, 0)), // -Z
            (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)),  // +X
            (new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0)),  // -X
            (new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)),  // +Y
            (new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),  // -Y
        };

        var vertices = new float[faces.Length * 4 * 12];
        var w = 0;
        foreach (var (normal, u, v) in faces)
        {
            var center = normal * 0.5f;
            for (var corner = 0; corner < 4; corner++)
            {
                var su = corner is 1 or 2 ? 0.5f : -0.5f;
                var sv = corner is 2 or 3 ? 0.5f : -0.5f;
                var pos = center + u * su + v * sv;
                vertices[w++] = pos.X; vertices[w++] = pos.Y; vertices[w++] = pos.Z;
                vertices[w++] = normal.X; vertices[w++] = normal.Y; vertices[w++] = normal.Z;
                vertices[w++] = su + 0.5f; vertices[w++] = sv + 0.5f;
                vertices[w++] = u.X; vertices[w++] = u.Y; vertices[w++] = u.Z; vertices[w++] = 1f; // tangent = face U axis
            }
        }

        var indices = new uint[faces.Length * 6];
        for (var face = 0; face < faces.Length; face++)
        {
            var b = (uint)(face * 4);
            var i = face * 6;
            indices[i + 0] = b;
            indices[i + 1] = b + 1;
            indices[i + 2] = b + 2;
            indices[i + 3] = b;
            indices[i + 4] = b + 2;
            indices[i + 5] = b + 3;
        }
        return (vertices, indices);
    }
}
