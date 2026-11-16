using System.Numerics;
using System.Runtime.CompilerServices;
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
        SceneData = new SdfSceneData(renderer);
        GiUniforms = renderer.CreateBuffer(new BufferDesc("SdfGiUniforms", GiBytes, BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    public static ulong GiBytes => (ulong)Unsafe.SizeOf<SdfGiUniformsGpu>();

    public IRenderer Renderer { get; }
    public GraphTextureRegistry Targets { get; }
    public BindGroupCache BindGroups { get; }
    public SamplerHandle LinearClampSampler { get; }

    /// <summary>The frame uniforms and shapes every field pass binds.</summary>
    public SdfSceneData SceneData { get; }

    /// <summary>The probe uniforms the GI feature writes in the frames it runs.</summary>
    public BufferHandle GiUniforms { get; }

    public uint Width { get; private set; }
    public uint Height { get; private set; }

    // Frame-local: set by RenderFrame before any feature runs.
    public SdfScene Scene { get; private set; } = null!;
    public Matrix4x4 ViewProjection { get; private set; }
    public Matrix4x4 InverseViewProjection { get; private set; }
    public Vector3 CameraPosition { get; private set; }

    public void BeginFrame(SdfScene scene)
    {
        var viewProjection = scene.Camera.View * scene.Camera.Projection;
        // The eye ray is unprojected per pixel, so a camera that cannot be inverted has no rays.
        if (!Matrix4x4.Invert(viewProjection, out var inverse) || !SdfMath.IsFinite(inverse))
            throw new ArgumentException("The SDF camera's view-projection must be finite and invertible.", nameof(scene));
        // The view maps the camera to the origin, so its inverse carries the origin to the camera.
        Matrix4x4.Invert(scene.Camera.View, out var cameraToWorld);
        Scene = scene;
        CameraPosition = cameraToWorld.Translation;
        ViewProjection = viewProjection;
        InverseViewProjection = inverse;
        SceneData.BeginFrame();
    }

    public void Resize(uint width, uint height)
    {
        Width = width;
        Height = height;
    }

    public TextureDesc FrameTarget(TextureFormat format) => Target(Width, Height, format);

    public static TextureDesc Target(uint width, uint height, TextureFormat format) => new(
        null, width, height, 1, 1, 1, TextureDimension.D2, format,
        TextureUsage.RenderAttachment | TextureUsage.TextureBinding);

    /// <summary>Records a fullscreen triangle with the pass's group 0.</summary>
    public static void DrawFullscreen(ref PassRecording pass, PipelineHandle pipeline)
    {
        pass.Encoder.SetPipeline(pipeline);
        pass.SetBindGroup(0);
        pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
    }

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
        Renderer.DestroyBuffer(GiUniforms);
        SceneData.Dispose();
        Renderer.DestroySampler(LinearClampSampler);
        BindGroups.Dispose();
        Targets.Dispose();
    }
}
