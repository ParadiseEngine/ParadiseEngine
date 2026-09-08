using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Integrates height and local fog extinction with shadowed direct-light scattering.</summary>
public sealed class FogFeature : IRenderFeature
{
    public const int MaxVolumes = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct FogUniforms
    {
        public Vector4 ColorDensity;
        public Vector4 HeightDistance;
        public Vector4 Scattering;
        public Vector4 Albedo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FogVolumeGpu
    {
        public Matrix4x4 InverseTransform;
        public Vector4 AlbedoDensity;
    }

    private readonly PbrContext _ctx;
    private readonly ShadowFeature _shadows;
    private readonly FogVolumeGpu[] _volumes = new FogVolumeGpu[MaxVolumes];
    private PipelineHandle _pipeline;
    private BindGroupLayoutDesc? _layout;
    private BindGroupLayoutDesc? _lightingLayout;
    private BufferHandle _uniformBuffer;
    private BufferHandle _volumeBuffer;

    internal FogFeature(PbrContext ctx, ShadowFeature shadows)
    {
        _ctx = ctx;
        _shadows = shadows;
    }

    public FeatureDefinition Definition => PbrFeatures.Fog;
    public FrameRequirements Requires => FrameRequirements.None;
    public int VolumeCount { get; private set; }

    public void Resize(uint width, uint height)
    {
        if (_pipeline.IsValid) EnsureTarget();
    }

    public void OnEnabledChanged(bool enabled) => VolumeCount = 0;

    private void EnsureTarget() => _ctx.Targets.Ensure(PbrTargets.FogColor, _ctx.FrameTarget(PbrTargets.HdrFormat));

    private void EnsureResources()
    {
        if (_pipeline.IsValid) return;
        var program = ShaderPrograms.Load("Shaders.fog");
        UniformLayoutValidator.ValidateBlock(program, "fog", (uint)Unsafe.SizeOf<FogUniforms>(),
            [("colorDensity", 0, 16), ("heightDistance", 16, 16), ("scattering", 32, 16), ("albedo", 48, 16)]);
        _layout = ShaderPrograms.FindGroup(program, 0);
        _lightingLayout = ShaderPrograms.FindGroup(program, 1);
        _pipeline = _ctx.Renderer.CreatePipeline(program, PbrTargets.HdrFormat);
        _uniformBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc("PbrFogUniforms", (ulong)Unsafe.SizeOf<FogUniforms>(),
            BufferUsage.Uniform | BufferUsage.CopyDst));
        _volumeBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc("PbrFogVolumes", (ulong)(Unsafe.SizeOf<FogVolumeGpu>() * MaxVolumes),
            BufferUsage.Storage | BufferUsage.CopyDst));
    }

    public void Setup(in FrameContext frame)
    {
        VolumeCount = 0;
        var settings = _ctx.Scene.Fog;
        if (!settings.Enabled || !frame.Blackboard.TryGet(PbrResults.SceneColor, out var source)) return;
        if (_ctx.Scene.FogVolumes.Count > MaxVolumes)
            throw new ArgumentException($"Fog supports at most {MaxVolumes} local volumes.");
        foreach (var volume in _ctx.Scene.FogVolumes)
        {
            if (!IsFinite(volume.Transform) || volume.Transform.M14 != 0 || volume.Transform.M24 != 0
                || volume.Transform.M34 != 0 || volume.Transform.M44 != 1
                || !Matrix4x4.Invert(volume.Transform, out var inverse) || !IsFinite(inverse))
                throw new ArgumentException("A fog volume transform must be finite, affine and invertible.");
            _volumes[VolumeCount++] = new FogVolumeGpu
            {
                InverseTransform = inverse,
                AlbedoDensity = new Vector4(Vector3.Min(FinitePositive(volume.Albedo), Vector3.One), FinitePositive(volume.Density)),
            };
        }
        EnsureResources();
        EnsureTarget();
        var maxDistance = MathF.Max(FinitePositive(settings.MaxDistance), 0.001f);
        var uniforms = new FogUniforms
        {
            ColorDensity = new Vector4(FinitePositive(settings.Color), FinitePositive(settings.Density)),
            HeightDistance = new Vector4(float.IsFinite(settings.BaseHeight) ? settings.BaseHeight : 0f,
                FinitePositive(settings.HeightFalloff), MathF.Min(FinitePositive(settings.StartDistance), maxDistance), maxDistance),
            Scattering = new Vector4(Math.Clamp(settings.Steps, 1, 128),
                float.IsFinite(settings.Anisotropy) ? Math.Clamp(settings.Anisotropy, -0.95f, 0.95f) : 0f,
                settings.LightScattering ? 1f : 0f, VolumeCount),
            Albedo = new Vector4(Vector3.Min(FinitePositive(settings.Albedo), Vector3.One),
                MathF.Abs(_ctx.Projection.M44) < 1e-6f ? 1f : 0f),
        };
        _ctx.Renderer.UpdateBuffer<FogUniforms>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
        if (VolumeCount > 0) _ctx.Renderer.UpdateBuffer<FogVolumeGpu>(_volumeBuffer, 0, _volumes.AsSpan(0, VolumeCount));
        var output = frame.Graph.Texture(PbrTargets.FogColor);
        frame.Graph.AddRasterPass("Fog.Integrate", RenderPassEvent.AfterTransparent, 50)
            .Color(0, output, LoadOp.Clear, clear: new ColorRgba(0, 0, 0, 0))
            .BindGroup(0, "PbrFog", _layout!,
            [
                GraphBinding.Texture(0, source),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
                GraphBinding.Texture(2, frame.Graph.Texture(PbrTargets.Depth)),
                GraphBinding.Buffer(3, _uniformBuffer, 0, (ulong)Unsafe.SizeOf<FogUniforms>()),
                GraphBinding.Buffer(4, _volumeBuffer, 0, (ulong)(Unsafe.SizeOf<FogVolumeGpu>() * MaxVolumes)),
            ])
            .BindGroup(1, "PbrFogLighting", _lightingLayout!,
            [
                GraphBinding.Buffer(0, _ctx.FrameUniformBuffer, 0, PbrContext.FrameUniformBytes),
                GraphBinding.TextureArray(1, frame.Graph.Texture(PbrTargets.ShadowArray)),
                GraphBinding.Sampler(2, _shadows.Sampler),
            ])
            .Record(this, Record);
        frame.Blackboard.Advance(PbrResults.SceneColor, source, output);
    }

    private static float FinitePositive(float value) => float.IsFinite(value) ? MathF.Max(value, 0f) : 0f;

    private static Vector3 FinitePositive(Vector3 value) =>
        new(FinitePositive(value.X), FinitePositive(value.Y), FinitePositive(value.Z));

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    private static void Record(FogFeature self, ref PassRecording pass, int _)
    {
        pass.Encoder.SetPipeline(self._pipeline);
        pass.SetBindGroup(0);
        pass.SetBindGroup(1);
        pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
    }

    public void Dispose()
    {
        if (!_pipeline.IsValid) return;
        _ctx.Renderer.DestroyPipeline(_pipeline);
        _ctx.Renderer.DestroyBuffer(_uniformBuffer);
        _ctx.Renderer.DestroyBuffer(_volumeBuffer);
    }
}
