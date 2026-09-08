using System;
using System.Collections.Generic;

namespace Paradise.Rendering.Graph;

/// <summary>Results one feature hands to a later one by name, for one frame.
///
/// <para>The alternative — feature A holding a reference to feature B — couples the two types
/// and stops a game-supplied feature from consuming an engine result. A name costs a dictionary
/// lookup per frame per consumer and lets a feature that is off simply not publish, so its
/// consumer falls back without knowing whether the producer exists.</para></summary>
public sealed class FrameBlackboard
{
    private readonly Dictionary<string, GraphTexture> _textures = new(StringComparer.Ordinal);

    public void Clear() => _textures.Clear();

    /// <summary>Publish <paramref name="texture"/> under <paramref name="name"/> for the rest of
    /// this frame. Publishing a name twice in one frame is an error: two producers of one result
    /// is a composition mistake, not a value to be last-writer-wins about.</summary>
    public void Publish(string name, GraphTexture texture)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_textures.TryAdd(name, texture))
            throw new InvalidOperationException($"'{name}' was already published this frame.");
    }

    /// <summary>Replace the current stage of a texture chain after consuming exactly that stage.</summary>
    public void Advance(string name, GraphTexture previous, GraphTexture next)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_textures.TryGetValue(name, out var current) || current != previous)
            throw new InvalidOperationException($"'{name}' no longer refers to the consumed texture.");
        if (!next.IsValid)
            throw new ArgumentException("A texture chain needs a valid next target.", nameof(next));
        if (previous == next)
            throw new ArgumentException("A texture chain must advance to a distinct target.", nameof(next));
        _textures[name] = next;
    }

    public bool TryGet(string name, out GraphTexture texture) => _textures.TryGetValue(name, out texture);

    /// <summary>The texture published under <paramref name="name"/>, or
    /// <paramref name="fallback"/> when nothing was — how a consumer says "not this frame".</summary>
    public GraphTexture GetOrDefault(string name, GraphTexture fallback) =>
        _textures.TryGetValue(name, out var texture) ? texture : fallback;
}
