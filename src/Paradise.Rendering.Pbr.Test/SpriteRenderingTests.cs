using System.Numerics;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The CPU half of sprite/voxel rendering: flipbook layout normalization and UV
/// addressing, quad/cube vertex emission in the 12-float interleaved layout, and the batch
/// index patterns — everything a dynamic sprite/voxel batch rewrites per frame.</summary>
public class SpriteRenderingTests
{
    [Test]
    public async Task flipbook_layout_normalizes_and_wraps_frames()
    {
        var layout = new FlipbookLayout(4, 2);
        await Assert.That(layout.FrameCount).IsEqualTo(8); // 0 → full grid

        var clamped = new FlipbookLayout(2, 2, frameCount: 9);
        await Assert.That(clamped.FrameCount).IsEqualTo(4); // never beyond the grid

        // Frame 5 of a 4×2 grid = column 1, row 1.
        var (min, max) = layout.UvRect(5);
        await Assert.That(min).IsEqualTo(new Vector2(0.25f, 0.5f));
        await Assert.That(max).IsEqualTo(new Vector2(0.5f, 1f));

        // Out-of-range frames wrap (looping flipbooks): 13 ≡ 5 (mod 8).
        await Assert.That(layout.UvRect(13)).IsEqualTo(layout.UvRect(5));
    }

    [Test]
    public async Task quad_vertices_carry_frame_uvs_and_face_the_viewer()
    {
        var vertices = new float[SpriteGeometry.QuadFloats];
        var layout = new FlipbookLayout(2, 2);
        SpriteGeometry.WriteQuad(
            vertices, 0, new Vector3(1f, 2f, 3f), Vector3.UnitX * 0.5f, Vector3.UnitY * 0.5f,
            layout, frame: 3); // column 1, row 1

        // Corner order TL, BL, BR, TR; row-major sheet, v grows downward.
        await Assert.That(new Vector3(vertices[0], vertices[1], vertices[2]))
            .IsEqualTo(new Vector3(0.5f, 2.5f, 3f)); // TL position
        await Assert.That((vertices[6], vertices[7])).IsEqualTo((0.5f, 0.5f));   // TL uv
        var br = 2 * SpriteGeometry.FloatsPerVertex;
        await Assert.That((vertices[br + 6], vertices[br + 7])).IsEqualTo((1f, 1f)); // BR uv

        // Normal = right × up = +Z (toward a camera whose axes were handed in).
        await Assert.That(new Vector3(vertices[3], vertices[4], vertices[5])).IsEqualTo(Vector3.UnitZ);
        // Tangent rides the right axis with w = 1.
        await Assert.That(new Vector3(vertices[8], vertices[9], vertices[10])).IsEqualTo(Vector3.UnitX);
        await Assert.That(vertices[11]).IsEqualTo(1f);
    }

    [Test]
    public async Task cube_vertices_span_the_extent_with_unit_face_normals()
    {
        var vertices = new float[SpriteGeometry.CubeFloats];
        SpriteGeometry.WriteCube(vertices, 0, new Vector3(10f, 0f, -5f), halfExtent: 0.25f);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var v = 0; v < 24; v++)
        {
            var offset = v * SpriteGeometry.FloatsPerVertex;
            var position = new Vector3(vertices[offset], vertices[offset + 1], vertices[offset + 2]);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
            var normal = new Vector3(vertices[offset + 3], vertices[offset + 4], vertices[offset + 5]);
            await Assert.That(MathF.Abs(normal.Length() - 1f)).IsLessThan(1e-6f);
        }
        await Assert.That(min).IsEqualTo(new Vector3(9.75f, -0.25f, -5.25f));
        await Assert.That(max).IsEqualTo(new Vector3(10.25f, 0.25f, -4.75f));
    }

    [Test]
    public async Task batch_indices_tile_the_quad_pattern()
    {
        var indices = SpriteGeometry.QuadIndices(3);
        await Assert.That(indices.Length).IsEqualTo(18);
        // Second quad: base vertex 4, same 0,1,2 / 0,2,3 fan.
        await Assert.That(indices[6..12]).IsEquivalentTo(new uint[] { 4, 5, 6, 4, 6, 7 });
        // A cube is 6 quads.
        await Assert.That(SpriteGeometry.CubeIndices(2).Length).IsEqualTo(2 * 6 * 6);
    }
}
