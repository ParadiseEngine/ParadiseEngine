namespace Paradise.Rendering.Pbr;

/// <summary>A square region in the shared shadow depth atlas, in texels.</summary>
public readonly record struct ShadowAtlasTile(uint X, uint Y, uint Size);

internal readonly record struct ShadowAtlasRequest(int Light, int Views, uint Resolution, int Priority);

/// <summary>Persistent, bounded atlas allocation. All faces of a light are admitted together;
/// budget pressure lowers their resolution together, then drops the whole light. Identical
/// requests retain their rectangles across frames. A deterministic priority order admits new
/// important lights before less important old allocations.</summary>
internal sealed class ShadowAtlasAllocator
{
    public const uint MinimumResolution = 128;
    private readonly Dictionary<int, ShadowAtlasTile[]> _previous = [];
    public Dictionary<int, ShadowAtlasTile[]> Allocate(uint size, IEnumerable<ShadowAtlasRequest> requests)
    {
        var cells = checked((int)(size / MinimumResolution));
        var occupied = new bool[cells * cells];
        var result = new Dictionary<int, ShadowAtlasTile[]>();
        foreach (var request in requests.OrderByDescending(r => r.Priority).ThenByDescending(r => r.Resolution).ThenBy(r => r.Light))
        {
            for (var resolution = Math.Min(size, RoundResolution(request.Resolution)); resolution >= MinimumResolution; resolution /= 2)
            {
                var tiles = new ShadowAtlasTile[request.Views];
                var count = 0;
                for (; count < tiles.Length; count++)
                {
                    ShadowAtlasTile? tile = null;
                    if (_previous.TryGetValue(request.Light, out var old) && count < old.Length && old[count].Size == resolution && Free(old[count]))
                        tile = old[count];
                    if (tile is null)
                    {
                        for (uint y = 0; y + resolution <= size && tile is null; y += resolution)
                            for (uint x = 0; x + resolution <= size; x += resolution)
                            {
                                var candidate = new ShadowAtlasTile(x, y, resolution);
                                if (!Free(candidate)) continue;
                                tile = candidate;
                                break;
                            }
                    }
                    if (tile is null) break;
                    tiles[count] = tile.Value;
                    Mark(tile.Value, true);
                }
                if (count == tiles.Length)
                {
                    result.Add(request.Light, tiles);
                    break;
                }
                for (var i = 0; i < count; i++) Mark(tiles[i], false);
            }
        }
        _previous.Clear();
        foreach (var (key, tiles) in result) _previous.Add(key, tiles);
        return result;

        bool Free(ShadowAtlasTile tile)
        {
            if (tile.X + tile.Size > size || tile.Y + tile.Size > size) return false;
            for (var y = tile.Y / MinimumResolution; y < (tile.Y + tile.Size) / MinimumResolution; y++)
                for (var x = tile.X / MinimumResolution; x < (tile.X + tile.Size) / MinimumResolution; x++)
                    if (occupied[y * cells + x]) return false;
            return true;
        }
        void Mark(ShadowAtlasTile tile, bool value)
        {
            for (var y = tile.Y / MinimumResolution; y < (tile.Y + tile.Size) / MinimumResolution; y++)
                for (var x = tile.X / MinimumResolution; x < (tile.X + tile.Size) / MinimumResolution; x++)
                    occupied[y * cells + x] = value;
        }
    }

    internal static uint RoundResolution(uint value)
    {
        uint result = MinimumResolution;
        while (result < value && result < 8192) result *= 2;
        return result;
    }
}
