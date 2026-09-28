using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The model a build cooked last, with the inputs reading it recorded.</summary>
/// <remarks>
/// Every mesh, skeleton and clip document of a model source cooks the whole model, so a character
/// with hundreds of clips re-read, re-hashed and re-cooked a 40 MB GLB once per clip. The build
/// walks paths in ordinal order, which keeps one source's documents adjacent: one entry catches
/// them while holding a single cooked model in memory. Replaying the inputs gives each document
/// the index entry its own read would have recorded.
/// </remarks>
internal sealed class CookedModelCache
{
    private UPath _model;
    private Guid? _asset;
    private CookedGlb? _cooked;
    private IReadOnlyList<BuildInput> _inputs = [];

    public CookedGlb? Find(UPath model, Guid? asset, out IReadOnlyList<BuildInput> inputs)
    {
        var hit = _cooked is not null && _model == model && _asset == asset;
        inputs = hit ? _inputs : [];
        return hit ? _cooked : null;
    }

    public void Keep(UPath model, Guid? asset, CookedGlb cooked, IReadOnlyList<BuildInput> inputs)
    {
        _model = model;
        _asset = asset;
        _cooked = cooked;
        _inputs = inputs;
    }
}
