using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Publishes the probe resources used to shade and visualize the current frame.</summary>
public readonly record struct ProbeFrameData(
    BufferHandle VolumeBuffer, ulong VolumeBufferBytes,
    BufferHandle ShadingStateBuffer, ulong ShadingStateBufferBytes,
    SamplerHandle Sampler, int ProbeCount,
    BufferHandle UpdateBuffer, ulong UpdateBufferBytes)
{
    public static readonly FrameDataKey<ProbeFrameData> Key = new("Pbr.Probes");
}

internal static class ProbeBindings
{
    /// <summary>Writes a program's group-3 probe bindings into the caller's buffer for that program.</summary>
    /// <remarks><see cref="FrameGraph.PassBuilder.BindGroup"/> copies the entries, so a feature keeps one
    /// buffer per program and refills it every frame instead of allocating it.</remarks>
    public static ReadOnlySpan<GraphBinding> Write(ref GraphBinding[] bindings,
        ShaderProgramDesc program, GraphTexture irradiance, GraphTexture visibility, in ProbeFrameData probes)
    {
        var layout = ShaderPrograms.FindGroup(program, 3);
        if (bindings.Length != layout.Entries.Length) bindings = new GraphBinding[layout.Entries.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            bindings[i] = layout.Entries[i].Binding switch
            {
                7 => GraphBinding.Texture(7, irradiance),
                8 => GraphBinding.Texture(8, visibility),
                9 => GraphBinding.Buffer(9, probes.VolumeBuffer, 0, probes.VolumeBufferBytes),
                10 => GraphBinding.Buffer(10, probes.ShadingStateBuffer, 0, probes.ShadingStateBufferBytes),
                11 => GraphBinding.Sampler(11, probes.Sampler),
                12 => GraphBinding.Buffer(12, probes.UpdateBuffer, 0, probes.UpdateBufferBytes),
                var other => throw new InvalidOperationException($"Probe program references unsupported probe binding {other}."),
            };
        }
        return bindings;
    }
}
