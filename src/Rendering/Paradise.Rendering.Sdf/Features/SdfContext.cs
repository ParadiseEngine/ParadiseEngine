using System.Numerics;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf;

/// <summary>Holds renderer resources and frame data shared by SDF features.</summary>
internal sealed class SdfContext : IDisposable
{
    public SdfContext(IRenderer renderer, uint width, uint height)
    {
        Renderer = renderer;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Targets = new GraphTextureRegistry(renderer);
        BindGroups = new BindGroupCache(renderer);
        LinearClampSampler = renderer.CreateSampler(new SamplerDesc(
            "SdfCompositeSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest));
    }

    public IRenderer Renderer { get; }
    public GraphTextureRegistry Targets { get; }
    public BindGroupCache BindGroups { get; }
    public SamplerHandle LinearClampSampler { get; }

    public uint Width { get; private set; }
    public uint Height { get; private set; }

    // Frame-local: set by RenderFrame before any feature runs.
    public SdfScene Scene { get; private set; } = null!;
    public Matrix4x4 ViewProjection { get; private set; }
    public Matrix4x4 InverseViewProjection { get; private set; }

    public void BeginFrame(SdfScene scene)
    {
        var viewProjection = scene.Camera.View * scene.Camera.Projection;
        // The eye ray is unprojected per pixel, so a camera that cannot be inverted has no rays.
        if (!Matrix4x4.Invert(viewProjection, out var inverse) || !SdfMath.IsFinite(inverse))
            throw new ArgumentException("The SDF camera's view-projection must be finite and invertible.", nameof(scene));
        Scene = scene;
        ViewProjection = viewProjection;
        InverseViewProjection = inverse;
    }

    public void Resize(uint width, uint height)
    {
        Width = width;
        Height = height;
    }

    public TextureDesc FrameTarget(TextureFormat format) => new(
        null, Width, Height, 1, 1, 1, TextureDimension.D2, format,
        TextureUsage.RenderAttachment | TextureUsage.TextureBinding);

    public static ShaderProgramDesc LoadProgram(string name) =>
        ShaderProgramLoader.Load(typeof(SdfRenderer).Assembly, name);

    public static BindGroupLayoutDesc FindGroup(ShaderProgramDesc program, uint groupIndex)
    {
        foreach (var group in program.Layout.Groups)
            if (group.GroupIndex == groupIndex) return group;
        throw new InvalidOperationException($"Program reflects no bind group {groupIndex}.");
    }

    public void Dispose()
    {
        Renderer.DestroySampler(LinearClampSampler);
        BindGroups.Dispose();
        Targets.Dispose();
    }
}
