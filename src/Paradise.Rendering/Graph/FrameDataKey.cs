using System;

namespace Paradise.Rendering.Graph;

/// <summary>Identifies a typed result shared by features within one frame.</summary>
/// <remarks>Identity belongs to the key instance, not its name; producers and consumers must share
/// the same key, normally declared as a static readonly field. The name is for diagnostics.</remarks>
public sealed class FrameDataKey<T>
{
    public FrameDataKey(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    public string Name { get; }
}
