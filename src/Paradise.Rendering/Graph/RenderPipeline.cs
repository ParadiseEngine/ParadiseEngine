using System;
using System.Collections.Generic;

namespace Paradise.Rendering.Graph;

/// <summary>The ordered features that make up a frame, and the driver that runs their setup.
///
/// <para>Order is the one thing this list encodes: a feature that consumes another's blackboard
/// result must come after it. Where a pass lands in the frame is the pass's event, not the
/// feature's position — two features at the same event keep their list order, which is the
/// tie-break the graph's stable sort promises.</para>
///
/// <para>Not a GPU pipeline: the name is the pipeline of features a frame is, in the sense
/// Unity's scriptable render pipeline uses the word.</para></summary>
public sealed class RenderPipeline : IDisposable
{
    private readonly List<IRenderFeature> _features = [];
    private readonly FrameBlackboard _blackboard = new();
    private bool _disposed;

    public IReadOnlyList<IRenderFeature> Features => _features;

    public RenderPipeline Add(IRenderFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _features.Add(feature);
        return this;
    }

    /// <summary>The first feature of type <typeparamref name="T"/>, for a host that configures
    /// one it did not construct.</summary>
    public T? Find<T>() where T : class, IRenderFeature
    {
        foreach (var feature in _features)
            if (feature is T match) return match;
        return null;
    }

    /// <summary>The union of every enabled feature's requirements.</summary>
    public FrameRequirements Requirements()
    {
        var requirements = FrameRequirements.None;
        foreach (var feature in _features)
            if (feature.Enabled) requirements |= feature.Requires;
        return requirements;
    }

    public void Resize(uint width, uint height)
    {
        foreach (var feature in _features) feature.Resize(width, height);
    }

    /// <summary>Run every enabled feature's setup in order against <paramref name="graph"/>,
    /// which the caller has already reset for this frame.</summary>
    public void Setup(FrameGraph graph, uint width, uint height)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _blackboard.Clear();
        var frame = new FrameContext(graph, width, height, Requirements(), _blackboard);
        foreach (var feature in _features)
            if (feature.Enabled) feature.Setup(in frame);
    }

    /// <summary>Features are disposed in reverse order, so a consumer goes before what it consumed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = _features.Count - 1; i >= 0; i--) _features[i].Dispose();
        _features.Clear();
    }
}
