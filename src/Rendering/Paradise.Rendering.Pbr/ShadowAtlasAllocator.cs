namespace Paradise.Rendering.Pbr;

/// <summary>A square region in the shared shadow depth atlas, in texels.</summary>
public readonly record struct ShadowAtlasTile(uint X, uint Y, uint Size);

internal readonly record struct ShadowAtlasRequest(int Light, int Views, uint Resolution, int Priority);

/// <summary>Persistent, bounded atlas allocation. All faces of a light are admitted together;
/// budget pressure lowers their resolution together, then drops the whole light. Identical
/// requests retain their rectangles across frames. A deterministic priority order admits new
/// important lights before less important old allocations.</summary>
/// <remarks>Shadows are planned every frame, so the occupancy grid, the admission order and every
/// light's tiles (this call's and the previous call's, swapped per call) are kept, not allocated.</remarks>
internal sealed class ShadowAtlasAllocator
{
    public const uint MinimumResolution = 128;
    /// <summary>A point light's six faces; cascades and spot lights need fewer.</summary>
    public const int MaxViewsPerLight = 6;
    private const int MaxLights = FrameUniformsGpu.MaxSceneLights;

    // Priority, then resolution, both descending, then light index: a total order over distinct
    // lights, so the unstable sort admits in the same order a stable one would.
    private static readonly Comparison<ShadowAtlasRequest> s_admission = static (a, b) =>
    {
        var order = b.Priority.CompareTo(a.Priority);
        if (order == 0) order = b.Resolution.CompareTo(a.Resolution);
        return order != 0 ? order : a.Light.CompareTo(b.Light);
    };

    private ShadowAtlasTile[] _tiles = new ShadowAtlasTile[MaxLights * MaxViewsPerLight];
    private ShadowAtlasTile[] _previousTiles = new ShadowAtlasTile[MaxLights * MaxViewsPerLight];
    private int[] _counts = new int[MaxLights];
    private int[] _previousCounts = new int[MaxLights];
    private bool[] _occupied = [];
    private ShadowAtlasRequest[] _order = [];
    private uint _size;
    private int _cells;

    /// <summary>Admits one request per light into a square atlas of <paramref name="size"/> texels;
    /// <see cref="Tiles"/> reads the result until the next call.</summary>
    public void Allocate(uint size, ReadOnlySpan<ShadowAtlasRequest> requests)
    {
        // Refused requests leave the last result and its retained rectangles as they were.
        Span<bool> requested = stackalloc bool[MaxLights];
        foreach (var request in requests)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)request.Light, (uint)MaxLights, nameof(requests));
            if (request.Views is < 1 or > MaxViewsPerLight)
                throw new ArgumentOutOfRangeException(nameof(requests), $"Light {request.Light} requests {request.Views} views; a light has 1 to {MaxViewsPerLight}.");
            if (requested[request.Light])
                throw new ArgumentException($"Light {request.Light} is requested twice.", nameof(requests));
            requested[request.Light] = true;
        }
        (_tiles, _previousTiles) = (_previousTiles, _tiles);
        (_counts, _previousCounts) = (_previousCounts, _counts);
        Array.Clear(_counts);
        _size = size;
        _cells = checked((int)(size / MinimumResolution));
        if (_occupied.Length != _cells * _cells) _occupied = new bool[_cells * _cells];
        else Array.Clear(_occupied);
        if (_order.Length < requests.Length) _order = new ShadowAtlasRequest[requests.Length];
        var order = _order.AsSpan(0, requests.Length);
        requests.CopyTo(order);
        order.Sort(s_admission);
        foreach (var request in order)
        {
            var tiles = _tiles.AsSpan(request.Light * MaxViewsPerLight, request.Views);
            var old = _previousTiles.AsSpan(request.Light * MaxViewsPerLight, _previousCounts[request.Light]);
            for (var resolution = Math.Min(size, RoundResolution(request.Resolution)); resolution >= MinimumResolution; resolution /= 2)
            {
                var count = 0;
                for (; count < tiles.Length; count++)
                {
                    var tile = count < old.Length && old[count].Size == resolution && Free(old[count]) ? old[count] : FirstFree(resolution);
                    if (tile is null) break;
                    tiles[count] = tile.Value;
                    Mark(tile.Value, true);
                }
                if (count == tiles.Length)
                {
                    _counts[request.Light] = count;
                    break;
                }
                for (var i = 0; i < count; i++) Mark(tiles[i], false);
            }
        }
    }

    /// <summary>The tiles the last <see cref="Allocate"/> admitted for <paramref name="light"/>; empty when it was dropped or not requested.</summary>
    public ReadOnlySpan<ShadowAtlasTile> Tiles(int light) => _tiles.AsSpan(light * MaxViewsPerLight, _counts[light]);

    private ShadowAtlasTile? FirstFree(uint resolution)
    {
        for (uint y = 0; y + resolution <= _size; y += resolution)
            for (uint x = 0; x + resolution <= _size; x += resolution)
            {
                var candidate = new ShadowAtlasTile(x, y, resolution);
                if (Free(candidate)) return candidate;
            }
        return null;
    }

    private bool Free(ShadowAtlasTile tile)
    {
        if (tile.X + tile.Size > _size || tile.Y + tile.Size > _size) return false;
        for (var y = tile.Y / MinimumResolution; y < (tile.Y + tile.Size) / MinimumResolution; y++)
            for (var x = tile.X / MinimumResolution; x < (tile.X + tile.Size) / MinimumResolution; x++)
                if (_occupied[y * _cells + x]) return false;
        return true;
    }

    private void Mark(ShadowAtlasTile tile, bool value)
    {
        for (var y = tile.Y / MinimumResolution; y < (tile.Y + tile.Size) / MinimumResolution; y++)
            for (var x = tile.X / MinimumResolution; x < (tile.X + tile.Size) / MinimumResolution; x++)
                _occupied[y * _cells + x] = value;
    }

    internal static uint RoundResolution(uint value)
    {
        uint result = MinimumResolution;
        while (result < value && result < 8192) result *= 2;
        return result;
    }
}
