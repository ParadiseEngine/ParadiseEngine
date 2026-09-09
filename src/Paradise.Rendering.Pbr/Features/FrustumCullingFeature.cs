using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Culls camera draws while preserving the complete shadow-caster and GI instance sets.</summary>
public sealed class FrustumCullingFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private bool[] _opaque = [];
    private bool[] _blend = [];
    private bool _active;

    internal FrustumCullingFeature(PbrContext ctx) => _ctx = ctx;
    public FeatureDefinition Definition => PbrFeatures.FrustumCulling;
    public FrameRequirements Requires => FrameRequirements.None;
    public int CulledDrawCount { get; private set; }

    internal bool OpaqueVisible(int index) => !_active || _opaque[index];
    internal bool BlendVisible(int index) => !_active || _blend[index];

    internal bool HasReliableBounds(PbrPrimitive primitive) => !primitive.Skinned && !primitive.Dynamic
        && (primitive.LocalMin != default || primitive.LocalMax != default)
        && _ctx.Materials.GetProgramId(primitive.MaterialId) == 0;

    public void Setup(in FrameContext frame)
    {
        _active = _ctx.Scene.Visibility.FrustumEnabled;
        CulledDrawCount = 0;
        if (!_active) return;
        if (_opaque.Length < _ctx.Opaque.Count) _opaque = new bool[DrawBufferCapacity.Grow(_opaque.Length, _ctx.Opaque.Count)];
        if (_blend.Length < _ctx.Blend.Count) _blend = new bool[DrawBufferCapacity.Grow(_blend.Length, _ctx.Blend.Count)];
        Fill(_ctx.Opaque, _opaque);
        Fill(_ctx.Blend, _blend);
    }

    private void Fill(List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> bucket, bool[] visible)
    {
        for (var i = 0; i < bucket.Count; i++)
        {
            var (instance, primitive, _) = bucket[i];
            var mvp = instance.Model * _ctx.ViewProjection;
            visible[i] = !HasReliableBounds(primitive) || Visibility.IntersectsFrustum(primitive.LocalMin, primitive.LocalMax, mvp);
            if (!visible[i]) CulledDrawCount++;
        }
    }

    public void OnEnabledChanged(bool enabled)
    {
        _active = false;
        CulledDrawCount = 0;
    }

    public void Resize(uint width, uint height) { }
    public void Dispose() { }
}
