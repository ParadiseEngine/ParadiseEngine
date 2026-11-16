namespace Paradise.Rendering.Browser;

/// <summary>Names of pipeline state in the JSON descriptor the shim hands to WebGPU.</summary>
/// <remarks>Platform-neutral so the wire contract is testable off the browser.</remarks>
internal static class PipelineJson
{
    /// <summary>The WebGPU <c>GPUCullMode</c> for <paramref name="cullMode"/>.</summary>
    public static string CullModeName(CullMode cullMode) => cullMode switch
    {
        CullMode.None => "none",
        CullMode.Front => "front",
        CullMode.Back => "back",
        _ => throw new NotSupportedException($"Cull mode '{cullMode}' has no WebGPU mapping."),
    };
}
