using System;

namespace Paradise.Rendering;

/// <summary>Raw-source shader creation parameters. Kept as the WGSL byte-stream path used by tests
/// and any consumer not going through the Slang reflection pipeline.</summary>
public readonly record struct ShaderDesc(
    string? Name,
    ShaderStage Stage,
    ReadOnlyMemory<byte> Source,
    string EntryPoint);
