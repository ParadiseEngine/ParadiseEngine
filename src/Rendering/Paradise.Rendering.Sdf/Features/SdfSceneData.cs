using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Sdf;

/// <summary>The scene as the field shaders read it: the frame uniforms and the packed shapes,
/// uploaded once a frame for every pass that traces the field.</summary>
internal sealed class SdfSceneData : IDisposable
{
    private readonly IRenderer _renderer;
    private readonly SdfField _field = new();
    private bool _uploaded;

    public SdfSceneData(IRenderer renderer)
    {
        _renderer = renderer;
        FrameBuffer = renderer.CreateBuffer(new BufferDesc(
            "SdfFrameUniforms", FrameBytes, BufferUsage.Uniform | BufferUsage.CopyDst));
        ShapeBuffer = renderer.CreateBuffer(new BufferDesc(
            "SdfShapes", ShapeBytes, BufferUsage.Storage | BufferUsage.CopyDst));
    }

    public static ulong FrameBytes => (ulong)Unsafe.SizeOf<SdfFrameUniformsGpu>();
    public static ulong ShapeBytes => (ulong)(Unsafe.SizeOf<SdfShapeGpu>() * SdfRenderer.MaxShapes);

    public BufferHandle FrameBuffer { get; }
    public BufferHandle ShapeBuffer { get; }
    public int ShapeCount { get; private set; }
    public int LightCount { get; private set; }

    public void BeginFrame() => _uploaded = false;

    /// <summary>Packs and uploads the frame's scene the first time a feature asks for it.</summary>
    public void Upload(SdfContext ctx)
    {
        if (_uploaded) return;
        var scene = ctx.Scene;
        if (scene.Shapes.Count > SdfRenderer.MaxShapes)
            throw new ArgumentException($"The SDF scene supports at most {SdfRenderer.MaxShapes} shapes.");
        if (scene.Lights.Count > SdfRenderer.MaxLights)
            throw new ArgumentException($"The SDF scene supports at most {SdfRenderer.MaxLights} lights.");

        _field.Set(CollectionsMarshal.AsSpan(scene.Shapes));
        ShapeCount = _field.Count;
        LightCount = scene.Lights.Count;
        if (ShapeCount > 0) _renderer.UpdateBuffer(ShapeBuffer, 0, _field.Shapes);

        var tracing = scene.Tracing;
        var sky = scene.Sky;
        var fog = scene.Fog;
        var reflections = scene.Reflections;
        var uniforms = new SdfFrameUniformsGpu
        {
            InvViewProj = ctx.InverseViewProjection,
            ViewProj = ctx.ViewProjection,
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
            FogColor = new Vector4(FinitePositive(fog.Color), fog.Enabled ? FinitePositive(fog.Density) : 0f),
            FogShape = new Vector4(FinitePositive(fog.HeightFalloff), float.IsFinite(fog.BaseHeight) ? fog.BaseHeight : 0f,
                FinitePositive(fog.StartDistance), float.IsFinite(fog.MaxOpacity) ? Math.Clamp(fog.MaxOpacity, 0f, 1f) : 1f),
            Reflections = new Vector4(
                reflections.Enabled && float.IsFinite(reflections.MaxRoughness) ? Math.Clamp(reflections.MaxRoughness, 0f, 1f) : -1f,
                Math.Clamp(reflections.MaxSteps, 1, 256),
                MathF.Max(FinitePositive(reflections.MaxDistance), 0.001f),
                reflections.Shadows ? 1f : 0f),
            CameraPosition = new Vector4(Finite(ctx.CameraPosition), 1f),
        };
        for (var i = 0; i < LightCount; i++) uniforms.Lights[i] = Pack(scene.Lights[i]);
        _renderer.UpdateBuffer<SdfFrameUniformsGpu>(FrameBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
        _uploaded = true;
    }

    internal static SdfLightGpu Pack(SdfLight light)
    {
        var direction = light.Direction;
        var length = direction.Length();
        // A degenerate direction lights from above rather than poisoning the frame with NaN.
        direction = float.IsFinite(length) && length > 1e-6f ? direction / length : Vector3.UnitY;
        // The cone narrows from the outer angle to the inner, which must stay the smaller.
        var outer = float.IsFinite(light.OuterConeAngle) ? Math.Clamp(light.OuterConeAngle, 0.002f, MathF.PI / 2f - 0.001f) : MathF.PI / 6f;
        var inner = float.IsFinite(light.InnerConeAngle) ? Math.Clamp(light.InnerConeAngle, 0f, outer - 0.001f) : 0f;
        return new SdfLightGpu
        {
            PositionType = new Vector4(Finite(light.Position), (float)light.Type),
            DirectionRange = new Vector4(direction, FinitePositive(light.Range)),
            ColorShadow = new Vector4(
                FinitePositive(light.Color) * FinitePositive(light.Intensity), light.CastsShadows ? 1f : 0f),
            Spot = new Vector4(MathF.Cos(outer), MathF.Cos(inner), 0f, 0f),
        };
    }

    private static float FinitePositive(float value) => float.IsFinite(value) ? MathF.Max(value, 0f) : 0f;

    private static Vector3 FinitePositive(Vector3 value) =>
        new(FinitePositive(value.X), FinitePositive(value.Y), FinitePositive(value.Z));

    private static Vector3 Finite(Vector3 value) => new(
        float.IsFinite(value.X) ? value.X : 0f, float.IsFinite(value.Y) ? value.Y : 0f, float.IsFinite(value.Z) ? value.Z : 0f);

    public void Dispose()
    {
        _renderer.DestroyBuffer(FrameBuffer);
        _renderer.DestroyBuffer(ShapeBuffer);
    }
}
