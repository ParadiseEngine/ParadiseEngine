using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>Forward+ binning, held from both ends: <see cref="ClusterBinning"/> against a
/// brute-force oracle, and lightCull.slang against <see cref="ClusterBinning"/>.
///
/// <para>The property that matters is one-sided. A froxel that claims a light it does not quite
/// touch costs a shading add the attenuation window zeroes anyway; a froxel that MISSES a light it
/// does touch drops that light from every pixel in it. So the oracle proves inclusion — a point
/// inside the froxel within the light's range means the bit must be set — and a second test proves
/// the masks are not simply full, which inclusion alone would satisfy.</para>
///
/// <para>The picture assertions are exact rather than approximate on purpose: the attenuation
/// window is <c>saturate(1 − (d/range)⁴)</c>, which is exactly zero at and beyond the range, so a
/// correctly binned frame and an unbinned one shade the same lights to the same bits. Any pixel
/// difference between culling on and off is a dropped light, not rounding.</para></summary>
public class LightCullingTests
{
    private const uint Size = 128;

    private static WebGpuRenderer? TryCreateHeadlessOrSkip()
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(Size, Size);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available on this host: {error.Message}");
            return null;
        }
    }

    // ---- CPU: the twin against brute force -------------------------------------------------

    private static (ClusterGrid Grid, float[] SliceDepths, CullLightGpu[] Lights) Fixture(
        int lightCount, int seed, uint width = 320, uint height = 240)
    {
        const float near = 0.1f;
        const float far = 50f;
        var projection = PbrMath.Perspective(MathF.PI / 3f, width / (float)height, near, far);
        var grid = ClusterGrid.For(projection, width, height, near, far);
        var depths = new float[ClusterBinning.ZSlices + 1];
        ClusterBinning.FillSliceDepths(near, far, depths);

        // View space: the camera is at the origin looking down −Z, so a light in front of it has
        // a negative Z. Spread them over the depth range the slices cover.
        var random = new Random(seed);
        var lights = new CullLightGpu[lightCount];
        for (var i = 0; i < lightCount; i++)
        {
            var depth = 0.5f + (float)random.NextDouble() * 25f;
            lights[i] = new CullLightGpu
            {
                ViewPositionRange = new Vector4(
                    ((float)random.NextDouble() * 2f - 1f) * depth,
                    ((float)random.NextDouble() * 2f - 1f) * depth,
                    -depth,
                    0.5f + (float)random.NextDouble() * 4f),
                Slot = (uint)i,
            };
        }
        return (grid, depths, lights);
    }

    /// <summary>A point inside the froxel, parameterised over its pixel rect and its slice.</summary>
    private static Vector3 PointInFroxel(
        in ClusterGrid grid, ReadOnlySpan<float> depths, int tileX, int tileY, int slice, float u, float v, float w)
    {
        var x = MathF.Min((tileX + u) * ClusterBinning.TileSize, grid.Width);
        var y = MathF.Min((tileY + v) * ClusterBinning.TileSize, grid.Height);
        var depth = depths[slice] + (depths[slice + 1] - depths[slice]) * w;

        var ndcX = x / grid.Width * 2f - 1f;
        var ndcY = 1f - y / grid.Height * 2f;
        var onNear = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), grid.InvProjection);
        var direction = new Vector3(onNear.X, onNear.Y, onNear.Z) / onNear.W;
        return direction * (depth / grid.Near);
    }

    [Test]
    public async Task every_light_that_reaches_a_froxel_has_its_bit_set()
    {
        var (grid, depths, lights) = Fixture(lightCount: 24, seed: 1);
        var masks = new uint[grid.FroxelCount * ClusterBinning.MaskWordsPerFroxel];
        ClusterBinning.Bin(grid, depths, lights, masks);

        var proved = 0;
        for (var froxel = 0; froxel < grid.FroxelCount; froxel++)
        {
            var tileX = froxel % grid.TilesX;
            var tileY = froxel / grid.TilesX % grid.TilesY;
            var slice = froxel / (grid.TilesX * grid.TilesY);
            for (var i = 0; i < lights.Length; i++)
            {
                var centre = new Vector3(
                    lights[i].ViewPositionRange.X, lights[i].ViewPositionRange.Y, lights[i].ViewPositionRange.Z);
                var range = lights[i].ViewPositionRange.W;
                var reaches = false;
                for (var s = 0; s < 27 && !reaches; s++)
                {
                    var point = PointInFroxel(
                        grid, depths, tileX, tileY, slice, s % 3 * 0.5f, s / 3 % 3 * 0.5f, s / 9 * 0.5f);
                    reaches = Vector3.DistanceSquared(point, centre) <= range * range;
                }
                if (!reaches) continue;

                proved++;
                var word = masks[froxel * ClusterBinning.MaskWordsPerFroxel + (i >> 5)];
                if ((word & (1u << (i & 31))) == 0)
                    throw new InvalidOperationException(
                        $"Froxel {froxel} (tile {tileX},{tileY} slice {slice}) contains a point within range of " +
                        $"light {i} at {centre} r={range}, but its bit is clear.");
            }
        }
        // The fixture actually exercises the claim rather than finding no overlaps at all.
        await Assert.That(proved).IsGreaterThan(200);
    }

    [Test]
    public async Task binning_culls_rather_than_setting_every_bit()
    {
        var (grid, depths, lights) = Fixture(lightCount: 24, seed: 2);
        var masks = new uint[grid.FroxelCount * ClusterBinning.MaskWordsPerFroxel];
        ClusterBinning.Bin(grid, depths, lights, masks);

        var set = 0L;
        var empty = 0;
        for (var froxel = 0; froxel < grid.FroxelCount; froxel++)
        {
            var bits = System.Numerics.BitOperations.PopCount(masks[froxel * 2])
                + System.Numerics.BitOperations.PopCount(masks[froxel * 2 + 1]);
            set += bits;
            if (bits == 0) empty++;
        }
        var average = set / (double)grid.FroxelCount;

        // Something is bound somewhere, the average froxel sees a small fraction of the lights,
        // and a real share of froxels see none at all. Loose bounds on purpose: the claim is that
        // binning discriminates, not that it produces one particular occupancy.
        await Assert.That(set).IsGreaterThan(0);
        await Assert.That(average).IsLessThan(lights.Length / 4.0);
        await Assert.That(empty).IsGreaterThan(grid.FroxelCount / 8);
    }

    [Test]
    public async Task a_slice_boundary_maps_back_to_its_own_slice()
    {
        const float near = 0.1f;
        const float far = 50f;
        var depths = new float[ClusterBinning.ZSlices + 1];
        ClusterBinning.FillSliceDepths(near, far, depths);

        await Assert.That(depths[0]).IsEqualTo(near).Within(1e-5f);
        await Assert.That(depths[ClusterBinning.ZSlices]).IsEqualTo(far).Within(1e-3f);
        await Assert.That(ClusterBinning.SliceOf(near, near, far)).IsEqualTo(0);
        await Assert.That(ClusterBinning.SliceOf(far, near, far)).IsEqualTo(ClusterBinning.ZSlices - 1);

        // The mid-point of every slice falls back into that slice: the shader's per-fragment
        // lookup and the boundaries the compute pass bins against are one mapping.
        for (var k = 0; k < ClusterBinning.ZSlices; k++)
        {
            var middle = MathF.Sqrt(depths[k] * depths[k + 1]); // geometric mean — the log-space centre
            var slice = ClusterBinning.SliceOf(middle, near, far);
            if (slice != k) throw new InvalidOperationException($"Slice {k} mid-point {middle} mapped to {slice}.");
        }
        await Assert.That(depths[1]).IsGreaterThan(depths[0]);
    }

    // ---- GPU: the shader against the twin --------------------------------------------------

    /// <summary>A floor under a row of point lights, each with a range small enough that the grid
    /// has something to cull.</summary>
    private static PbrScene BuildScene(PbrRenderer pbr, int lights)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var materialId = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.8f, 0.8f, 1f));
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, materialId)]);

        var eye = new Vector3(0f, 4f, 7f);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = eye,
            },
            Ambient = new PbrAmbient { Sky = Vector3.Zero, Equator = Vector3.Zero, Ground = Vector3.Zero, Flat = true },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
        };
        scene.Instances.Add(new PbrInstance
        {
            Mesh = mesh,
            Model = Matrix4x4.CreateScale(new Vector3(12f, 0.1f, 12f)) * Matrix4x4.CreateTranslation(0f, -0.05f, 0f),
        });
        for (var i = 0; i < lights; i++)
        {
            var angle = i * MathF.Tau / lights;
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Point,
                Position = new Vector3(MathF.Cos(angle) * 4f, 0.9f, MathF.Sin(angle) * 4f),
                Color = new Vector3(1f, 0.9f, 0.8f),
                Intensity = 4f,
                Range = 3f,
            });
        }
        return scene;
    }

    [Test]
    public async Task the_compute_pass_bins_exactly_what_the_cpu_twin_does()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var culling = pbr.Pipeline.Find<LightCullingFeature>()!;

        var scene = BuildScene(pbr, lights: 12);
        pbr.RenderFrame(scene);

        // Span cannot cross an await (CS4007), so the frame's state is materialised first.
        var lights = culling.LightsForTest.ToArray();
        var grid = culling.GridForTest;
        var depths = new float[ClusterBinning.ZSlices + 1];
        ClusterBinning.FillSliceDepths(culling.Near, culling.Far, depths);
        var expected = new uint[grid.FroxelCount * ClusterBinning.MaskWordsPerFroxel];
        ClusterBinning.Bin(grid, depths, lights, expected);

        var bytes = backend.ReadbackBuffer(culling.ClusterBuffer, 0, culling.ClusterBufferBytes);
        var actual = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();

        await Assert.That(lights.Length).IsEqualTo(12);
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        var differing = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i] == expected[i]) continue;
            if (differing++ == 0)
                throw new InvalidOperationException(
                    $"Mask word {i} (froxel {i / 2}, word {i % 2}): shader 0x{actual[i]:x8}, twin 0x{expected[i]:x8}.");
        }
        // Not vacuous: the shader bound something.
        var set = 0L;
        foreach (var word in actual) set += System.Numerics.BitOperations.PopCount(word);
        await Assert.That(set).IsGreaterThan(0);
    }

    [Test]
    public async Task culling_switched_off_leaves_the_frame_and_not_the_picture()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;
        var switches = new FeatureSwitches();
        using var pbr = new PbrRenderer(backend, switches, Size, Size);

        var scene = BuildScene(pbr, lights: 12);
        pbr.RenderFrame(scene);
        var passesOn = Passes(pbr, "LightCull");
        var pixelsOn = (byte[])backend.ReadbackColor(out var width, out var height).Clone();

        switches.Set(PbrFeatures.LightCulling.Id, false);
        pbr.RenderFrame(scene);
        var passesOff = Passes(pbr, "LightCull");
        var pixelsOff = (byte[])backend.ReadbackColor(out var offWidth, out var offHeight).Clone();

        switches.Set(PbrFeatures.LightCulling.Id, true);
        pbr.RenderFrame(scene);
        var passesBack = Passes(pbr, "LightCull");

        await Assert.That((width, height)).IsEqualTo((offWidth, offHeight));
        await Assert.That(passesOn).IsEqualTo(1);
        await Assert.That(passesOff).IsEqualTo(0);
        await Assert.That(passesBack).IsEqualTo(1);
        // The switch moves cost, never pixels. This is also what catches a retraction that did not
        // happen: switched off with a stale mask buffer still bound, lights would go missing.
        await Assert.That(pixelsOff).IsEquivalentTo(pixelsOn);
    }

    [Test]
    public async Task a_moving_camera_keeps_lighting_the_scene_after_the_switch_goes_off()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;
        var switches = new FeatureSwitches();
        using var pbr = new PbrRenderer(backend, switches, Size, Size);

        // Bin once from here, then switch off and move: the masks left in the buffer describe a
        // camera that no longer exists, so a frame that still trusted them would go dark.
        var scene = BuildScene(pbr, lights: 12);
        pbr.RenderFrame(scene);

        switches.Set(PbrFeatures.LightCulling.Id, false);
        var eye = new Vector3(6f, 3f, -6f);
        scene.Camera = scene.Camera with { View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY), Position = eye };
        pbr.RenderFrame(scene);
        var moved = Brightness(backend);

        // The same view, from a renderer that never had culling on, is what "lit correctly" means.
        using var reference = new PbrRenderer(backend, Never(), Size, Size);
        var referenceScene = BuildScene(reference, lights: 12);
        referenceScene.Camera = scene.Camera;
        reference.RenderFrame(referenceScene);
        var expected = Brightness(backend);

        await Assert.That(moved).IsEqualTo(expected).Within(0.01);
        await Assert.That(moved).IsGreaterThan(1.0);
    }

    private static FeatureSwitches Never()
    {
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.LightCulling.Id, false);
        return switches;
    }

    private static int Passes(PbrRenderer pbr, string prefix)
    {
        var count = 0;
        foreach (var name in pbr.LastPassNames)
            if (name.StartsWith(prefix, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>Mean channel value of the presented frame — enough to tell a lit picture from an
    /// unlit one without pinning pixels to an adapter.</summary>
    private static double Brightness(WebGpuRenderer backend)
    {
        var pixels = backend.ReadbackColor(out _, out _);
        long total = 0;
        foreach (var value in pixels) total += value;
        return (double)total / pixels.Length;
    }
}
