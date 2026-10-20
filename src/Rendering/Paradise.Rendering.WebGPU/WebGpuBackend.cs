namespace Paradise.Rendering.WebGPU;

/// <summary>Selects the native graphics API used by Dawn.</summary>
/// <remarks>An explicit backend is required, not preferred: an unavailable backend does not fall back to another API.</remarks>
public enum WebGpuBackend
{
    /// <summary>Leaves backend selection to Dawn, preserving its platform defaults.</summary>
    Default,

    /// <summary>Requires a Vulkan adapter with WebGPU core support.</summary>
    Vulkan,

    /// <summary>Requires a Direct3D 12 adapter with WebGPU core support.</summary>
    Direct3D12,

    /// <summary>Requires a Metal adapter with WebGPU core support.</summary>
    Metal,
}
