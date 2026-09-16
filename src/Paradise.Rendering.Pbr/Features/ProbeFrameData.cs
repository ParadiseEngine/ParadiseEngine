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
    public static GraphBinding[] Create(
        ShaderProgramDesc program, GraphTexture irradiance, GraphTexture visibility, in ProbeFrameData probes)
    {
        var layout = ShaderPrograms.FindGroup(program, 3);
        var bindings = new GraphBinding[layout.Entries.Length];
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
