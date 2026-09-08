using System.Numerics;

namespace Paradise.Rendering.Pbr.Test;

public class ShadowMathTests
{
    [Test]
    public async Task practical_splits_span_the_camera_range_and_interpolate_uniform_and_logarithmic_spacing()
    {
        await Assert.That(CascadedShadowMath.Split(1, 100, 2, 4, 0)).IsEqualTo(50.5f);
        await Assert.That(CascadedShadowMath.Split(1, 100, 2, 4, 1)).IsEqualTo(10f);
        await Assert.That(CascadedShadowMath.Split(1, 100, 2, 4, 0.5f)).IsEqualTo(30.25f);
        for (var count = 1; count <= 4; count++)
        {
            var previous = CascadedShadowMath.Split(0.1f, 100, 0, count, 0.65f);
            await Assert.That(previous).IsEqualTo(0.1f).Within(1e-5f);
            for (var i = 1; i <= count; i++)
            {
                var next = CascadedShadowMath.Split(0.1f, 100, i, count, 0.65f);
                await Assert.That(next).IsGreaterThan(previous);
                previous = next;
            }
            await Assert.That(previous).IsEqualTo(100f).Within(1e-3f);
        }
    }

    [Test]
    public async Task cascade_fit_contains_perspective_and_orthographic_receivers_including_infinite_far_cameras()
    {
        var eye = new Vector3(3, 2, 5);
        var view = PbrMath.LookAt(eye, new Vector3(0, 1, 0), Vector3.UnitY);
        Matrix4x4.Invert(view, out var inverseView);
        foreach (var projection in new[]
        {
            PbrMath.Perspective(MathF.PI / 2, 1.5f, 0.1f, 200),
            PbrMath.Orthographic(12, 1.5f, 0.1f, 200),
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1.5f, 0.1f, float.PositiveInfinity),
        })
        {
            var camera = new PbrCamera { View = view, Projection = projection, Position = eye };
            var matrix = CascadedShadowMath.Fit(camera, Vector3.Normalize(new Vector3(1, 3, 2)),
                2, 25, Vector3.Zero, new Vector3(100), 1022, out var texel, out var depth);
            await Assert.That(texel).IsGreaterThan(0f);
            await Assert.That(depth.Y).IsGreaterThan(depth.X);
            foreach (var z in new[] { 2f, 25f })
                foreach (var x in new[] { -1f, 1f })
                    foreach (var y in new[] { -1f, 1f })
                    {
                        // Construct receiver corners independently from the authored FOV/size.
                        var halfHeight = projection.M34 == 0 ? 6 : z;
                        var world = Vector3.Transform(new Vector3(x * halfHeight * 1.5f, y * halfHeight, -z), inverseView);
                        var clip = Vector4.Transform(new Vector4(world, 1), matrix);
                        await Assert.That(MathF.Abs(clip.X / clip.W)).IsLessThanOrEqualTo(1f);
                        await Assert.That(MathF.Abs(clip.Y / clip.W)).IsLessThanOrEqualTo(1f);
                        await Assert.That(clip.Z / clip.W).IsGreaterThanOrEqualTo(0f);
                        await Assert.That(clip.Z / clip.W).IsLessThanOrEqualTo(1f);
                    }
        }
    }

    [Test]
    public async Task cascade_projection_is_stable_during_sub_texel_lateral_camera_motion()
    {
        var camera = new PbrCamera { View = Matrix4x4.Identity, Projection = PbrMath.Perspective(MathF.PI / 2, 1, 0.1f, 100) };
        var a = CascadedShadowMath.Fit(camera, Vector3.UnitY, 1, 10, Vector3.Zero, new Vector3(100), 1022, out var texel, out _);
        var shift = new Vector3(texel * 0.2f, 0, 0);
        var moved = camera with { View = Matrix4x4.CreateTranslation(-shift), Position = shift };
        var b = CascadedShadowMath.Fit(moved, Vector3.UnitY, 1, 10, Vector3.Zero, new Vector3(100), 1022, out var nextTexel, out _);
        await Assert.That(nextTexel).IsEqualTo(texel);
        foreach (var point in new[] { Vector3.Zero, new Vector3(3, 2, -5) })
        {
            var p = Vector3.Transform(point, a);
            var q = Vector3.Transform(point, b);
            await Assert.That(q.X).IsEqualTo(p.X).Within(1e-6f);
            await Assert.That(q.Y).IsEqualTo(p.Y).Within(1e-6f);
        }
    }

    [Test]
    public async Task atlas_reduces_whole_point_lights_then_rejects_them_without_partial_faces()
    {
        var allocator = new ShadowAtlasAllocator();
        var allocations = allocator.Allocate(512,
        [
            new ShadowAtlasRequest(0, 1, 256, 20),
            new ShadowAtlasRequest(1, 6, 512, 10),
            new ShadowAtlasRequest(2, 6, 512, 0),
            new ShadowAtlasRequest(3, 1, 128, -1),
        ]);
        await Assert.That(allocations[0][0].Size).IsEqualTo(256u);
        await Assert.That(allocations[1].Length).IsEqualTo(6);
        await Assert.That(allocations[1].All(t => t.Size == 128)).IsTrue();
        await Assert.That(allocations.ContainsKey(2)).IsTrue();
        await Assert.That(allocations.ContainsKey(3)).IsFalse();
        await CheckTiles(512, allocations.Values.SelectMany(t => t)).ConfigureAwait(false);
        var rejected = allocator.Allocate(256, [new ShadowAtlasRequest(0, 6, 256, 1), new ShadowAtlasRequest(1, 1, 256, 0)]);
        await Assert.That(rejected.ContainsKey(0)).IsFalse();
        await Assert.That(rejected[1][0].Size).IsEqualTo(256u);
    }

    [Test]
    public async Task atlas_preserves_steady_allocations_and_reuses_freed_tiles_without_overlap()
    {
        var allocator = new ShadowAtlasAllocator();
        ShadowAtlasRequest[] requests = [new(0, 4, 256, 1), new(1, 6, 256, 0), new(2, 1, 512, 0)];
        var first = allocator.Allocate(1024, requests);
        var same = allocator.Allocate(1024, requests.Reverse());
        foreach (var key in first.Keys)
            await Assert.That(first[key].SequenceEqual(same[key])).IsTrue();
        var changed = allocator.Allocate(1024, [requests[0], requests[2], new(3, 6, 256, 0)]);
        await Assert.That(changed.ContainsKey(1)).IsFalse();
        await Assert.That(changed.ContainsKey(3)).IsTrue();
        await CheckTiles(1024, changed.Values.SelectMany(t => t)).ConfigureAwait(false);
        var shrunk = allocator.Allocate(512, requests);
        await CheckTiles(512, shrunk.Values.SelectMany(t => t)).ConfigureAwait(false);
        var important = allocator.Allocate(512, [new(0, 1, 512, -1), new(9, 1, 512, 10)]);
        await Assert.That(important.Keys.Single()).IsEqualTo(9);
    }

    private static async Task CheckTiles(uint size, IEnumerable<ShadowAtlasTile> source)
    {
        var tiles = source.ToArray();
        for (var i = 0; i < tiles.Length; i++)
        {
            var a = tiles[i];
            await Assert.That(a.X + a.Size).IsLessThanOrEqualTo(size);
            await Assert.That(a.Y + a.Size).IsLessThanOrEqualTo(size);
            for (var j = 0; j < i; j++)
            {
                var b = tiles[j];
                await Assert.That(a.X >= b.X + b.Size || b.X >= a.X + a.Size ||
                                 a.Y >= b.Y + b.Size || b.Y >= a.Y + a.Size).IsTrue();
            }
        }
    }
}
