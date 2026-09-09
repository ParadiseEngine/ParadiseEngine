using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Maps a moving logical grid onto fixed physical probe and atlas slots.</summary>
internal sealed class ProbeGrid
{
    private int[] _reset = [];

    public PbrProbeVolume? Volume { get; private set; }
    public (int X, int Y, int Z) Scroll { get; private set; }
    public int Count => Volume is { } v ? v.CountX * v.CountY * v.CountZ : 0;
    public int ResetCount { get; private set; }
    public ReadOnlySpan<int> ResetIndices => _reset.AsSpan(0, ResetCount);

    public bool SetVolume(PbrProbeVolume requested, bool scrolling)
    {
        ResetCount = 0;
        if (Volume is not { } previous || !SameShape(previous, requested))
            return Reset(requested);

        if (!scrolling)
        {
            var tolerance = 0.1f * MathF.Min(previous.Spacing.X, MathF.Min(previous.Spacing.Y, previous.Spacing.Z));
            return (previous.Origin - requested.Origin).Length() > tolerance && Reset(requested);
        }

        // Quantize relative to the resident grid, including negative movement. Sub-cell camera
        // motion must neither move existing probes nor spend their convergence history.
        var delta = (requested.Origin - previous.Origin) / previous.Spacing;
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z)) return Reset(requested);
        if (MathF.Abs(delta.X) >= previous.CountX || MathF.Abs(delta.Y) >= previous.CountY || MathF.Abs(delta.Z) >= previous.CountZ)
            return Reset(requested with { Origin = previous.Origin + new Vector3(MathF.Truncate(delta.X), MathF.Truncate(delta.Y), MathF.Truncate(delta.Z)) * previous.Spacing });
        var dx = (int)delta.X;
        var dy = (int)delta.Y;
        var dz = (int)delta.Z;
        if (dx == 0 && dy == 0 && dz == 0) return false;

        Volume = previous with { Origin = previous.Origin + new Vector3(dx, dy, dz) * previous.Spacing };
        Scroll = (Wrap(Scroll.X + dx, previous.CountX), Wrap(Scroll.Y + dy, previous.CountY), Wrap(Scroll.Z + dz, previous.CountZ));
        for (var z = 0; z < previous.CountZ; z++)
        for (var y = 0; y < previous.CountY; y++)
        for (var x = 0; x < previous.CountX; x++)
        {
            if (x + dx < 0 || x + dx >= previous.CountX || y + dy < 0 || y + dy >= previous.CountY || z + dz < 0 || z + dz >= previous.CountZ)
                _reset[ResetCount++] = Index(x, y, z);
        }
        return false;
    }

    public int Index(int x, int y, int z)
    {
        var v = Volume!;
        return Wrap(x + Scroll.X, v.CountX) + v.CountX * (Wrap(y + Scroll.Y, v.CountY) + v.CountY * Wrap(z + Scroll.Z, v.CountZ));
    }

    public Vector3 Position(int index)
    {
        var v = Volume!;
        var x = Wrap(index % v.CountX - Scroll.X, v.CountX);
        var y = Wrap(index / v.CountX % v.CountY - Scroll.Y, v.CountY);
        var z = Wrap(index / (v.CountX * v.CountY) - Scroll.Z, v.CountZ);
        return v.Origin + new Vector3(x, y, z) * v.Spacing;
    }

    private bool Reset(PbrProbeVolume volume)
    {
        Volume = volume;
        Scroll = default;
        if (_reset.Length != Count) _reset = new int[Count];
        for (var i = 0; i < Count; i++) _reset[i] = i;
        ResetCount = Count;
        return true;
    }

    private static bool SameShape(PbrProbeVolume a, PbrProbeVolume b) =>
        a.CountX == b.CountX && a.CountY == b.CountY && a.CountZ == b.CountZ && a.Spacing == b.Spacing;

    private static int Wrap(int value, int count) => (value % count + count) % count;
}
