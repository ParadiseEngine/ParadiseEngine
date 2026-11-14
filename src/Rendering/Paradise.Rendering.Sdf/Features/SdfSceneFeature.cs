using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf;

/// <summary>Sphere-traces the scene's distance field into HDR color and device depth.</summary>
public sealed class SdfSceneFeature : IRenderFeature
{
    private readonly SdfContext _ctx;
    private readonly SdfField _field = new();
    private readonly PipelineHandle _pipeline;
    private readonly BindGroupLayoutDesc _layout;
    private readonly BufferHandle _frameBuffer;
    private readonly BufferHandle _shapeBuffer;

    internal SdfSceneFeature(SdfContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;
        var program = SdfContext.LoadProgram("Shaders.sdfScene");
        SdfUniformLayout.ValidateScene(program);
        _layout = SdfContext.FindGroup(program, 0);
        // Every fragment writes its own depth, including 1 for the sky, so nothing may be rejected.
        _pipeline = renderer.CreatePipeline(program, SdfTargets.HdrFormat,
            depthStencilFormat: SdfTargets.DepthFormat, depthWriteEnabled: true, depthCompare: CompareFunction.Always);
        _frameBuffer = renderer.CreateBuffer(new BufferDesc(
            "SdfFrameUniforms", FrameBytes, BufferUsage.Uniform | BufferUsage.CopyDst));
        _shapeBuffer = renderer.CreateBuffer(new BufferDesc(
            "SdfShapes", ShapeBytes, BufferUsage.Storage | BufferUsage.CopyDst));
    }

    private static ulong FrameBytes => (ulong)Unsafe.SizeOf<SdfFrameUniformsGpu>();
    private static ulong ShapeBytes => (ulong)(Unsafe.SizeOf<SdfShapeGpu>() * SdfRenderer.MaxShapes);

    public FeatureDefinition Definition => SdfFeatures.Scene;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Shapes traced by the last frame this feature ran in.</summary>
    public int ShapeCount { get; private set; }

    /// <summary>Lights shaded by the last frame this feature ran in.</summary>
    public int LightCount { get; private set; }

    public void Resize(uint width, uint height)
    {
        _ctx.Targets.Ensure(SdfTargets.Hdr, _ctx.FrameTarget(SdfTargets.HdrFormat));
        _ctx.Targets.Ensure(SdfTargets.Depth, _ctx.FrameTarget(SdfTargets.DepthFormat));
    }

    public void OnEnabledChanged(bool enabled)
    {
        ShapeCount = 0;
        LightCount = 0;
    }

    public void Setup(in FrameContext frame)
    {
        var scene = _ctx.Scene;
        if (scene.Shapes.Count > SdfRenderer.MaxShapes)
            throw new ArgumentException($"The SDF scene supports at most {SdfRenderer.MaxShapes} shapes.");
        if (scene.Lights.Count > SdfRenderer.MaxLights)
            throw new ArgumentException($"The SDF scene supports at most {SdfRenderer.MaxLights} lights.");

        _field.Set(CollectionsMarshal.AsSpan(scene.Shapes));
        ShapeCount = _field.Count;
        LightCount = scene.Lights.Count;
        if (ShapeCount > 0) _ctx.Renderer.UpdateBuffer(_shapeBuffer, 0, _field.Shapes);

        var tracing = scene.Tracing;
        var sky = scene.Sky;
        var uniforms = new SdfFrameUniformsGpu
        {
            InvViewProj = _ctx.InverseViewProjection,
            ViewProj = _ctx.ViewProjection,
            Counts = new Vector4(ShapeCount, LightCount, Math.Clamp(tracing.MaxSteps, 1, 512),
                MathF.Max(FinitePositive(tracing.MaxDistance), 0.001f)),
            Trace = new Vector4(
                float.IsFinite(tracing.SurfaceEpsilon) ? Math.Clamp(tracing.SurfaceEpsilon, 1e-5f, 0.1f) : 0.001f,
                tracing.Shadows ? MathF.Max(FinitePositive(tracing.ShadowSharpness), 0.01f) : 0f,
                MathF.Min(FinitePositive(tracing.AmbientOcclusion), 1f),
                FinitePositive(sky.AmbientIntensity)),
            SkyZenith = new Vector4(FinitePositive(sky.Zenith), 0f),
            SkyHorizon = new Vector4(FinitePositive(sky.Horizon), 0f),
            SkyGround = new Vector4(FinitePositive(sky.Ground), 0f),
        };
        for (var i = 0; i < LightCount; i++) uniforms.Lights[i] = Pack(scene.Lights[i]);
        _ctx.Renderer.UpdateBuffer<SdfFrameUniformsGpu>(_frameBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        var graph = frame.Graph;
        var hdr = graph.Texture(SdfTargets.Hdr);
        graph.AddRasterPass("Sdf.Scene", RenderPassEvent.Opaque)
            .Color(0, hdr, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 1f))
            .Depth(graph.Texture(SdfTargets.Depth), LoadOp.Clear, clear: 1f)
            .BindGroup(0, "SdfScene", _layout,
            [
                GraphBinding.Buffer(0, _frameBuffer, 0, FrameBytes),
                GraphBinding.Buffer(1, _shapeBuffer, 0, ShapeBytes),
            ])
            .Record(this, Record);
        frame.Blackboard.Publish(SdfResults.SceneColor, hdr);
    }

    private static SdfLightGpu Pack(SdfLight light)
    {
        var direction = light.Direction;
        var length = direction.Length();
        // A degenerate direction lights from above rather than poisoning the frame with NaN.
        direction = float.IsFinite(length) && length > 1e-6f ? direction / length : Vector3.UnitY;
        return new SdfLightGpu
        {
            PositionType = new Vector4(Finite(light.Position), (float)light.Type),
            DirectionRange = new Vector4(direction, FinitePositive(light.Range)),
            ColorShadow = new Vector4(
                FinitePositive(light.Color) * FinitePositive(light.Intensity), light.CastsShadows ? 1f : 0f),
        };
    }

    private static float FinitePositive(float value) => float.IsFinite(value) ? MathF.Max(value, 0f) : 0f;

    private static Vector3 FinitePositive(Vector3 value) =>
        new(FinitePositive(value.X), FinitePositive(value.Y), FinitePositive(value.Z));

    private static Vector3 Finite(Vector3 value) => new(
        float.IsFinite(value.X) ? value.X : 0f, float.IsFinite(value.Y) ? value.Y : 0f, float.IsFinite(value.Z) ? value.Z : 0f);

    private static void Record(SdfSceneFeature self, ref PassRecording pass, int _)
    {
        pass.Encoder.SetPipeline(self._pipeline);
        pass.SetBindGroup(0);
        pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
    }

    public void Dispose()
    {
        _ctx.Renderer.DestroyPipeline(_pipeline);
        _ctx.Renderer.DestroyBuffer(_frameBuffer);
        _ctx.Renderer.DestroyBuffer(_shapeBuffer);
    }
}
