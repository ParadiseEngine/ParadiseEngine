using System;

namespace Paradise.Rendering;

/// <summary>Raw-source shader creation parameters. Kept as the WGSL byte-stream path used by tests
/// and any consumer not going through the Slang reflection pipeline.</summary>
public readonly struct ShaderDesc
{
    public readonly string? Name;
    public readonly ShaderStage Stage;
    public readonly ReadOnlyMemory<byte> Source;
    public readonly string EntryPoint;

    public ShaderDesc(string? name, ShaderStage stage, ReadOnlyMemory<byte> source, string entryPoint)
    {
        Name = name;
        Stage = stage;
        Source = source;
        EntryPoint = entryPoint;
    }
}
