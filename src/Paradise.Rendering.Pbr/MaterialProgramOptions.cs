namespace Paradise.Rendering.Pbr;

/// <summary>Explicit guarantees and optional entry points for a custom material program.</summary>
public readonly record struct MaterialProgramOptions
{
    /// <summary>The vertex shader stays inside the primitive's uploaded local bounds.</summary>
    public bool PreservesMeshBounds { get; init; }

    /// <summary>Opaque draws may regroup with other opted-in draws when scene reordering is enabled.</summary>
    public bool AllowsOpaqueReordering { get; init; }

    /// <summary>The shader covers the stock rigid geometry without displacement or fragment discard.</summary>
    /// <remarks>This permits the stock depth path to treat the material as a solid occluder;
    /// reliable bounds and instancing alone do not imply solid coverage.</remarks>
    public bool OpaqueCoverage { get; init; }

    /// <summary>An optional rigid vertex entry that reads per-instance draws through Common/pbrInstancing.slang.</summary>
    public string? InstancedVertexEntryPoint { get; init; }

    /// <summary>An optional fragment entry taking InstancedFragmentInput and reading pbrInstanceDraw(input.instanceDrawIndex).</summary>
    /// <remarks>When omitted, instanced draws use the ordinary fragment entry. That entry must
    /// not read the single-draw uniform; varyings already carry the instance highlight flags.</remarks>
    public string? InstancedFragmentEntryPoint { get; init; }
}
