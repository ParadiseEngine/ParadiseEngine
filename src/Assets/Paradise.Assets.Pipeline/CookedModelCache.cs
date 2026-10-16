using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The model a build cooked last, with the inputs reading it recorded.</summary>
/// <remarks>
/// Consecutive documents naming the same source and model reuse one cooked result, limiting
/// retained memory to one model. Ordinal path order often groups extracted documents, but moved
/// or rerouted documents may miss this cache. Inputs are replayed for every cache hit so each
/// document records the dependencies its own model read would have observed.
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
