using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr;

/// <summary>One point or spot light as Forward+ binning sees it. Mirrors <c>CullLight</c> in
/// lightCull.slang byte for byte.</summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
internal struct CullLightGpu
{
    /// <summary>xyz: the light's centre in VIEW space, w: its range.</summary>
    public Vector4 ViewPositionRange;

    /// <summary>Index in <c>frame.sceneLights</c> — the bit this light owns in a froxel's mask.
    /// Carried rather than implied by position, because directional lights are left out of the
    /// array entirely and the remaining indices are therefore not contiguous.</summary>
    public uint Slot;

    private uint _pad0;
    private uint _pad1;
    private uint _pad2;
}

/// <summary>The froxel grid one frame is binned against: everything the assignment depends on
/// besides the lights themselves.</summary>
internal readonly record struct ClusterGrid(
    Matrix4x4 InvProjection, uint Width, uint Height, int TilesX, int TilesY, float Near, float Far)
{
    public int FroxelCount => TilesX * TilesY * ClusterBinning.ZSlices;

    /// <summary>The grid covering a frame of this size under this projection.</summary>
    public static ClusterGrid For(in Matrix4x4 projection, uint width, uint height, float near, float far)
    {
        var invProjection = Matrix4x4.Invert(projection, out var inverse) ? inverse : Matrix4x4.Identity;
        return new ClusterGrid(
            invProjection, Math.Max(1, width), Math.Max(1, height),
            ClusterBinning.TilesFor(width), ClusterBinning.TilesFor(height), near, far);
    }
}

/// <summary>Provides the CPU reference for lightCull.slang bounds and sphere tests.</summary>
/// <remarks>Used by tests against the shader and a brute-force oracle; production binning runs on
/// the GPU.</remarks>
internal static class ClusterBinning
{
    /// <summary>Froxel tile edge in pixels. Godot's cluster shape.</summary>
    public const int TileSize = 32;

    /// <summary>Logarithmic depth slices per froxel column.</summary>
    public const int ZSlices = 32;

    /// <summary>Mask words per froxel: 64 lights at one bit each.</summary>
    public const int MaskWordsPerFroxel = 2;

    public static int TilesFor(uint pixels) => (int)Math.Max(1u, (pixels + TileSize - 1) / TileSize);

    /// <summary>The <see cref="ZSlices"/> + 1 slice boundaries, as positive view distances:
    /// <c>near·(far/near)^(k/N)</c>, the inverse of <see cref="SliceOf"/>.
    ///
    /// <para>These are computed once and uploaded rather than derived in the shader, because
    /// WGSL's <c>pow</c> and <c>MathF.Pow</c> agree only to a few ULP and a light sitting on a
    /// boundary would then land in different froxels on the two sides.</para></summary>
    public static void FillSliceDepths(float near, float far, Span<float> depths)
    {
        var ratio = far / near;
        for (var k = 0; k <= ZSlices; k++)
        {
            depths[k] = near * MathF.Pow(ratio, k / (float)ZSlices);
        }
    }

    /// <summary>Which slice a view depth falls in — the mapping pbrCore.slang performs per
    /// fragment to find its froxel. Paired with <see cref="FillSliceDepths"/>; a test pins the
    /// round trip.</summary>
    public static int SliceOf(float viewZ, float near, float far) =>
        Math.Clamp(
            (int)(MathF.Log(Math.Max(viewZ, near) / near) / MathF.Log(far / near) * ZSlices),
            0, ZSlices - 1);

    /// <summary>A tile corner in view space, on the near plane. The camera is at the origin
    /// looking down −Z, so the point doubles as the direction of the ray through that corner.</summary>
    private static Vector3 UnprojectCorner(in ClusterGrid grid, float pixelX, float pixelY)
    {
        var ndcX = pixelX / grid.Width * 2f - 1f;
        var ndcY = 1f - pixelY / grid.Height * 2f;
        var view = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), grid.InvProjection);
        return new Vector3(view.X, view.Y, view.Z) / view.W;
    }

    /// <summary>The view-space bounding box of one froxel. Conservative: the box around the
    /// froxel's frustum, never smaller than it.</summary>
    public static void FroxelBounds(
        in ClusterGrid grid, ReadOnlySpan<float> sliceDepths, int tileX, int tileY, int slice,
        out Vector3 min, out Vector3 max)
    {
        var x0 = tileX * (float)TileSize;
        var y0 = tileY * (float)TileSize;
        var x1 = MathF.Min(x0 + TileSize, grid.Width);
        var y1 = MathF.Min(y0 + TileSize, grid.Height);

        var corner00 = UnprojectCorner(grid, x0, y0);
        var corner10 = UnprojectCorner(grid, x1, y0);
        var corner01 = UnprojectCorner(grid, x0, y1);
        var corner11 = UnprojectCorner(grid, x1, y1);

        var scaleNear = sliceDepths[slice] / grid.Near;
        var scaleFar = sliceDepths[slice + 1] / grid.Near;

        Span<Vector3> points =
        [
            corner00 * scaleNear, corner10 * scaleNear, corner01 * scaleNear, corner11 * scaleNear,
            corner00 * scaleFar, corner10 * scaleFar, corner01 * scaleFar, corner11 * scaleFar,
        ];
        min = points[0];
        max = points[0];
        foreach (var point in points)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }

    /// <summary>Fills every froxel's mask words, exactly as the compute pass does. <paramref name="masks"/>
    /// holds <see cref="MaskWordsPerFroxel"/> words per froxel in the shader's froxel order,
    /// <c>((slice · tilesY + tileY) · tilesX + tileX)</c>.</summary>
    public static void Bin(
        in ClusterGrid grid, ReadOnlySpan<float> sliceDepths, ReadOnlySpan<CullLightGpu> lights, Span<uint> masks)
    {
        for (var froxel = 0; froxel < grid.FroxelCount; froxel++)
        {
            var tileX = froxel % grid.TilesX;
            var tileY = froxel / grid.TilesX % grid.TilesY;
            var slice = froxel / (grid.TilesX * grid.TilesY);
            FroxelBounds(grid, sliceDepths, tileX, tileY, slice, out var min, out var max);

            var mask0 = 0u;
            var mask1 = 0u;
            foreach (var light in lights)
            {
                var centre = new Vector3(light.ViewPositionRange.X, light.ViewPositionRange.Y, light.ViewPositionRange.Z);
                var delta = centre - Vector3.Clamp(centre, min, max);
                var range = light.ViewPositionRange.W;
                if (Vector3.Dot(delta, delta) > range * range) continue;
                if (light.Slot < 32) mask0 |= 1u << (int)light.Slot;
                else mask1 |= 1u << (int)(light.Slot - 32);
            }
            masks[froxel * MaskWordsPerFroxel] = mask0;
            masks[froxel * MaskWordsPerFroxel + 1] = mask1;
        }
    }
}
