using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Paradise.Rendering.Graph;

/// <summary>Shares named textures and typed data between features for one frame.</summary>
/// <remarks>A disabled producer does not publish, so consumers can fall back without depending
/// on its feature type. Typed data slots retain capacity between frames without boxing values;
/// clearing releases published values and makes every result absent.</remarks>
public sealed class FrameBlackboard
{
    private abstract class DataSlot
    {
        public abstract void Clear();
    }

    private sealed class DataSlot<T> : DataSlot
    {
        public bool HasValue;
        public T Value = default!;

        public override void Clear()
        {
            HasValue = false;
            Value = default!;
        }
    }

    private readonly Dictionary<string, GraphTexture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<object, DataSlot> _data = new(ReferenceEqualityComparer.Instance);

    public void Clear()
    {
        _textures.Clear();
        foreach (var entry in _data) entry.Value.Clear();
    }

    /// <summary>Publishes one typed result, rejecting a second producer for the same key this frame.</summary>
    public void Publish<T>(FrameDataKey<T> key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_data.TryGetValue(key, out var entry))
        {
            entry = new DataSlot<T>();
            _data.Add(key, entry);
        }
        var slot = (DataSlot<T>)entry;
        if (slot.HasValue)
            throw new InvalidOperationException($"'{key.Name}' was already published this frame.");
        slot.Value = value;
        slot.HasValue = true;
    }

    /// <summary>Reads the typed result published for this key in the current frame.</summary>
    public bool TryGet<T>(FrameDataKey<T> key, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_data.TryGetValue(key, out var entry))
        {
            var slot = (DataSlot<T>)entry;
            if (slot.HasValue)
            {
                value = slot.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>Reads the current typed result, or the fallback when no producer published it.</summary>
    public T GetOrDefault<T>(FrameDataKey<T> key, T fallback) => TryGet(key, out var value) ? value : fallback;

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
