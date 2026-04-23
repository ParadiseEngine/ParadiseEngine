namespace Paradise.Rendering;

/// <summary>Raw-source shader creation parameters. Kept as the WGSL byte-stream path used by tests
/// and any consumer not going through the Slang reflection pipeline.</summary>
/// <remarks>Equality is reference-based on <paramref name="Source"/> (record-struct synthesized
/// equality compares the array reference, not its contents). Consumers caching shader modules
/// keyed by <see cref="ShaderDesc"/> should hash the byte content upstream, or wrap this descriptor
/// with their own content-hashed key, before reusing it across distinct array allocations.</remarks>
public readonly record struct ShaderDesc(
    string? Name,
    ShaderStage Stage,
    byte[] Source,
    string EntryPoint);
