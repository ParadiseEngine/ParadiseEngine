using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>CPU reference for conservative homogeneous frustum and screen-depth bounds.</summary>
public static class Visibility
{
    public const int TileSize = 8;
    public const float DepthBias = 0.00002f;

    /// <summary>Reject only when all eight corners lie outside the same WebGPU clip plane.</summary>
    public static bool IntersectsFrustum(Vector3 min, Vector3 max, in Matrix4x4 transform)
    {
        if (!Valid(min, max)) return true;
        var outside = 63;
        for (var i = 0; i < 8; i++)
        {
            var p = Vector4.Transform(new Vector4(Corner(min, max, i), 1f), transform);
            if (!Finite(p)) return true;
            var epsilon = MathF.Max(1f, MathF.Abs(p.W)) * 1e-5f;
            var mask = (p.X < -p.W - epsilon ? 1 : 0) | (p.X > p.W + epsilon ? 2 : 0)
                | (p.Y < -p.W - epsilon ? 4 : 0) | (p.Y > p.W + epsilon ? 8 : 0)
                | (p.Z < -epsilon ? 16 : 0) | (p.Z > p.W + epsilon ? 32 : 0);
            outside &= mask;
        }
        return outside == 0;
    }

    /// <summary>Project a box in front of the near plane to an expanded pixel rectangle and nearest depth.</summary>
    public static bool TryProject(Vector3 min, Vector3 max, in Matrix4x4 transform,
        uint width, uint height, out Vector4 rectangle, out float nearest)
    {
        rectangle = default;
        nearest = 1f;
        if (!Valid(min, max) || width == 0 || height == 0) return false;
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        for (var i = 0; i < 8; i++)
        {
            var p = Vector4.Transform(new Vector4(Corner(min, max, i), 1f), transform);
            // Clipping through the eye or near plane needs clipped edges, so leave such draws visible.
            if (!Finite(p) || p.W <= 1e-5f || p.Z <= 0f) return false;
            var xy = new Vector2((p.X / p.W * 0.5f + 0.5f) * width, (0.5f - p.Y / p.W * 0.5f) * height);
            lo = Vector2.Min(lo, xy);
            hi = Vector2.Max(hi, xy);
            nearest = MathF.Min(nearest, p.Z / p.W);
        }
        if (hi.X < 0f || hi.Y < 0f || lo.X >= width || lo.Y >= height) return false;
        lo = Vector2.Clamp(new Vector2(MathF.Floor(lo.X - 1f), MathF.Floor(lo.Y - 1f)), Vector2.Zero, new Vector2(width - 1, height - 1));
        hi = Vector2.Clamp(new Vector2(MathF.Ceiling(hi.X + 1f), MathF.Ceiling(hi.Y + 1f)), Vector2.Zero, new Vector2(width - 1, height - 1));
        rectangle = new Vector4(lo, hi.X, hi.Y);
        nearest -= DepthBias;
        return true;
    }

    /// <summary>Max reduction keeps a background or uncovered pixel from acting as an occluder.</summary>
    public static void ReduceDepth(ReadOnlySpan<float> depth, int width, int height, Span<float> tiles)
    {
        var tilesX = (width + TileSize - 1) / TileSize;
        var tilesY = (height + TileSize - 1) / TileSize;
        if (width <= 0 || height <= 0 || depth.Length < width * height || tiles.Length < tilesX * tilesY)
            throw new ArgumentException("Depth and tile spans must cover the image.");
        for (var y = 0; y < tilesY; y++)
        for (var x = 0; x < tilesX; x++)
        {
            var farthest = 0f;
            for (var py = y * TileSize; py < Math.Min((y + 1) * TileSize, height); py++)
            for (var px = x * TileSize; px < Math.Min((x + 1) * TileSize, width); px++)
                farthest = MathF.Max(farthest, depth[py * width + px]);
            tiles[y * tilesX + x] = farthest;
        }
    }

    /// <summary>A draw is occluded only if its closest point lies behind every touched depth tile.</summary>
    public static bool IsOccluded(Vector4 rectangle, float nearest, ReadOnlySpan<float> tiles, int tilesX)
    {
        for (var y = (int)rectangle.Y / TileSize; y <= (int)rectangle.W / TileSize; y++)
        for (var x = (int)rectangle.X / TileSize; x <= (int)rectangle.Z / TileSize; x++)
            if (nearest <= tiles[y * tilesX + x]) return false;
        return true;
    }

    private static bool Valid(Vector3 min, Vector3 max) =>
        float.IsFinite(min.X) && float.IsFinite(min.Y) && float.IsFinite(min.Z)
        && float.IsFinite(max.X) && float.IsFinite(max.Y) && float.IsFinite(max.Z)
        && min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;

    private static bool Finite(Vector4 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && float.IsFinite(p.W);

    private static Vector3 Corner(Vector3 min, Vector3 max, int i) =>
        new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
}
