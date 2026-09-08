using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Stabilized directional cascades and a dynamic atlas shared with local lights.
/// Each tile is rendered through its own viewport in one depth pass. A whole light's faces are
/// admitted together; atlas pressure lowers their resolution before dropping the light.</summary>
public sealed class ShadowFeature : IRenderFeature
{
    private const uint DefaultMapSize = 1024;
    // Metadata and texture budgets are independent of the number of unshadowed lights.
    private const int MaxViews = FrameUniformsGpu.MaxShadowViews;

    private readonly PbrContext _ctx;
    private readonly ShaderProgramDesc _program;
    private readonly PipelineHandle _pipeline;
    private PipelineHandle _skinnedPipeline;
    private readonly BufferHandle _drawRing;
    private readonly BindGroupHandle _drawGroup;
    private readonly BindGroupHandle _jointGroup;
    private readonly byte[] _staging;
    private uint _mapSize = DefaultMapSize;
    private float _blurTexels = 3f;
    private uint _allocatedAtlasSize;
    private uint _atlasSize = 4096;
    private readonly ShadowAtlasAllocator _atlas = new();
    private int _stagedDraws;

    private readonly List<ShadowView> _views = [];
    private readonly int[] _firstView = new int[FrameUniformsGpu.MaxSceneLights];
    private readonly int[] _viewCount = new int[FrameUniformsGpu.MaxSceneLights];
    // Retained in the light record for custom shaders; built-ins use per-view texel sizes.
    private readonly float[] _texelWorld = new float[FrameUniformsGpu.MaxSceneLights];

    internal ShadowFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;

        // Shader taps clamp to tile texel centers before hardware comparison filtering.
        Sampler = renderer.CreateSampler(new SamplerDesc(
            "PbrShadowSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest,
            MaxAnisotropy: 1, Compare: CompareFunction.LessEqual));

        // A valid array must always exist even when nothing casts, because the scene's frame
        // group binds it unconditionally; hence the minimum of one layer.
        EnsureAtlas();
        // Plan() is where "no light has a tile" is normally established, and a build with shadows
        // switched off never runs one — so the invariant is established here too, rather than
        // resting on _firstView's zeroes, which mean "view 0" and not "no view".
        ClearPlan();

        // Depth-only caster pipeline. Its group-0 draw UBO is a dynamic-offset ring like the main
        // one; the vertex layout reads position from the full interleaved mesh stride (shadow.slang
        // declares only location 0).
        _program = ShaderPrograms.WithDynamicDrawRing(ShaderPrograms.Load("Shaders.shadow"));
        _pipeline = renderer.CreateDepthOnlyPipeline(_program, TextureFormat.Depth32Float,
            ShaderPrograms.PositionOnlyLayout(ctx.Programs.MeshStride));

        var ringDesc = new BufferDesc("PbrShadowDrawRing", (ulong)ctx.DrawStride * PbrContext.MaxDrawsPerFrame, BufferUsage.Uniform | BufferUsage.CopyDst);
        _drawRing = renderer.CreateBuffer(in ringDesc);
        _staging = new byte[ctx.DrawStride * PbrContext.MaxDrawsPerFrame];
        _drawGroup = renderer.CreateBindGroup(new BindGroupDesc("PbrShadowDrawGroup", ShaderPrograms.FindGroup(_program, 0), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _drawRing, 0, (ulong)Unsafe.SizeOf<ShadowDrawUniformsGpu>()),
        }));
        _jointGroup = renderer.CreateBindGroup(new BindGroupDesc("PbrShadowJointGroup", ShaderPrograms.FindGroup(_program, 1), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, ctx.JointBuffer, 0, ctx.JointBufferBytes),
        }));
    }

    public FeatureDefinition Definition => PbrFeatures.Shadows;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Number of parallel camera-depth slices for a directional light, 1..4.</summary>
    public int CascadeCount { get; set; } = 4;
    /// <summary>Practical split blend: 0 is uniform, 1 logarithmic.</summary>
    public float CascadeSplitLambda { get; set; } = 0.65f;
    /// <summary>Maximum camera depth receiving directional shadows, in metres.</summary>
    public float MaxDistance { get; set; } = 100f;
    /// <summary>Fraction of a cascade blended into the following cascade, 0..0.3.</summary>
    public float CascadeBlend { get; set; } = 0.1f;
    /// <summary>Shared atlas extent (power of two, 512..8192). Memory is bounded by this value,
    /// irrespective of the number of lights. Local lights and directional cascades share it.</summary>
    public uint AtlasSize
    {
        get => _atlasSize;
        set => _atlasSize = Math.Clamp(ShadowAtlasAllocator.RoundResolution(value), 512u, 8192u);
    }
    /// <summary>The most recent frame's admitted views, for diagnostics and atlas inspection.</summary>
    public IReadOnlyList<ShadowView> Views => _views;

    /// <summary>Requested directional tile resolution and automatic local-light ceiling, rounded
    /// up to a power of two within 256..8192; atlas pressure may reduce it.</summary>
    public uint MapSize
    {
        get => _mapSize;
        set
        {
            var clamped = Math.Clamp(ShadowAtlasAllocator.RoundResolution(value), 256u, 8192u);
            if (clamped == _mapSize) return;
            _mapSize = clamped;
        }
    }

    /// <summary>Maximum PCSS search and filter radius in shadow texels, clamped to 0.5..32.</summary>
    public float BlurTexels
    {
        get => _blurTexels;
        set => _blurTexels = Math.Clamp(value, 0.5f, 32f);
    }

    /// <summary>Compatibility alias for half of <see cref="MaxDistance"/>.</summary>
    public float DirectionalRadius
    {
        get => MaxDistance * 0.5f;
        set => MaxDistance = value > 0 ? value * 2f : float.MaxValue;
    }

    /// <summary>The comparison sampler the scene pass reads the array with.</summary>
    public SamplerHandle Sampler { get; }

    // The frame's shadow plan, valid after Setup until the next frame.

    internal int FirstView(int light) => _firstView[light];
    internal int ViewCount(int light) => _viewCount[light];
    internal float TexelWorld(int light) => _texelWorld[light];

    public void Resize(uint width, uint height)
    {
        // Shadow maps are sized by MapSize, not by the frame.
    }

    /// <summary>The plan outlives the frame that built it — the scene's frame uniforms are
    /// written from it — so a feature switched off mid-run has to retract it, or every light
    /// keeps sampling a tile nobody is filling any more.</summary>
    public void OnEnabledChanged(bool enabled)
    {
        if (!enabled) ClearPlan();
    }

    private void ClearPlan()
    {
        _views.Clear();
        Array.Fill(_firstView, -1);
        Array.Clear(_viewCount);
    }

    public void Setup(in FrameContext frame)
    {
        Plan(_ctx.Scene, _ctx.View);
        EnsureAtlas();

        // Ring budget: views × casters. Hard-fail up front — like the main-pass check — so a
        // partial fill (silently missing shadows) cannot ship.
        var total = _views.Count * _ctx.Opaque.Count;
        if (total > PbrContext.MaxDrawsPerFrame)
            throw new InvalidOperationException(
                $"{total} shadow-caster draws ({_views.Count} views × {_ctx.Opaque.Count} casters) exceed the {PbrContext.MaxDrawsPerFrame}-slot shadow ring.");

        _stagedDraws = 0;
        var array = frame.Graph.Texture(PbrTargets.ShadowArray);
        if (_views.Count > 0)
            frame.Graph.AddRasterPass("Shadow.Atlas", RenderPassEvent.Shadows)
                .DepthLayer(array, 0, LoadOp.Clear, clear: 1f)
                .Record(this, RecordAtlas);
    }

    /// <summary>Upload the caster uniforms the recorders staged. Here rather than at the end of
    /// <see cref="Setup"/> because staging happens while the graph RECORDS, which is inside the
    /// compile — and before the submit, because that is when the GPU reads the ring.</summary>
    public void BeforeSubmit()
    {
        if (_stagedDraws > 0)
            _ctx.Renderer.UpdateBuffer<byte>(_drawRing, 0, _staging.AsSpan(0, _stagedDraws * (int)_ctx.DrawStride));
    }

    // Matrix indices remain compact regardless of where the allocator places the tiles.
    private void Plan(PbrScene scene, in Matrix4x4 view)
    {
        ClearPlan();
        if (_ctx.Opaque.Count == 0) return;

        ComputeWorldBounds(out var center, out var extent);
        // The camera's world position anchors the directional fit; a non-invertible view falls
        // back to the scene centre, which degrades to the whole-scene fit rather than anything wrong.
        var cameraPosition = Matrix4x4.Invert(view, out var viewInverse)
            ? viewInverse.Translation
            : center;
        var requests = new List<ShadowAtlasRequest>();
        var cascadeCount = Math.Clamp(CascadeCount, 1, 4);
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            var light = scene.Lights[i];
            if (!light.CastsShadows) continue;
            var count = light.Type == PbrLightType.Directional ? cascadeCount : light.Type == PbrLightType.Point ? 6 : 1;
            var resolution = light.Type == PbrLightType.Directional ? _mapSize : light.ShadowResolution;
            if (resolution == 0)
            {
                // Projected angular extent chooses local-light resolution. Quantized powers of
                // two and retained rectangles avoid needless movement on steady scenes.
                var distance = MathF.Max(1f, Vector3.Distance(cameraPosition, light.Position));
                resolution = (uint)Math.Clamp(_mapSize * light.Range / distance, 128f, _mapSize);
            }
            requests.Add(new ShadowAtlasRequest(i, count, resolution, light.ShadowPriority));
        }
        var tiles = _atlas.Allocate(_atlasSize, requests);
        var (cameraNear, cameraFar) = CascadedShadowMath.CameraRange(scene.Camera.Projection);
        var shadowFar = MathF.Max(cameraNear + 0.01f, MathF.Min(cameraFar, MathF.Max(MaxDistance, cameraNear + 0.01f)));
        foreach (var request in requests)
        {
            if (!tiles.TryGetValue(request.Light, out var lightTiles) || _views.Count + lightTiles.Length > MaxViews) continue;
            var light = scene.Lights[request.Light];
            _firstView[request.Light] = _views.Count; // matrix/view index, distinct from physical layer
            _viewCount[request.Light] = lightTiles.Length;
            for (var f = 0; f < lightTiles.Length; f++)
            {
                var tile = lightTiles[f];
                var resolution = tile.Size - 2; // a clear one-texel guard around every viewport
                float texelWorld, splitNear = 0, splitFar = 0;
                Vector2 depth;
                Matrix4x4 vp;
                if (light.Type == PbrLightType.Directional)
                {
                    splitNear = CascadedShadowMath.Split(cameraNear, shadowFar, f, cascadeCount, CascadeSplitLambda);
                    splitFar = CascadedShadowMath.Split(cameraNear, shadowFar, f + 1, cascadeCount, CascadeSplitLambda);
                    var overlapNear = f == 0 ? splitNear : splitNear -
                        (splitNear - CascadedShadowMath.Split(cameraNear, shadowFar, f - 1, cascadeCount, CascadeSplitLambda)) * Math.Clamp(CascadeBlend, 0, 0.3f);
                    vp = CascadedShadowMath.Fit(scene.Camera, light.Direction, overlapNear, splitFar,
                        center, extent, resolution, out texelWorld, out depth);
                }
                else
                {
                    vp = ComputeLocalLightMatrix(light, f, resolution, out texelWorld);
                    depth = new Vector2(0.05f, MathF.Max(light.Range, 1f));
                }
                _views.Add(new ShadowView(request.Light, f, tile, vp, texelWorld, splitNear, splitFar,
                    depth, light.Type != PbrLightType.Directional));
                if (f == 0) _texelWorld[request.Light] = texelWorld;
            }
        }
    }

    private void EnsureAtlas()
    {
        if (_allocatedAtlasSize == _atlasSize) return;
        // Keep the array binding contract used by raster, GI and game shaders, with one layer.
        _ctx.Targets.Ensure(PbrTargets.ShadowArray,
            PbrTargets.RenderTarget(_atlasSize, _atlasSize, TextureFormat.Depth32Float, 1));
        _allocatedAtlasSize = _atlasSize;
    }

    private static void RecordAtlas(ShadowFeature self, ref PassRecording pass, int _)
    {
        for (var view = 0; view < self._views.Count; view++) RecordView(self, ref pass, view);
    }

    private static void RecordView(ShadowFeature self, ref PassRecording pass, int view)
    {
        ref var encoder = ref pass.Encoder;
        var item = self._views[view];
        var vp = item.Vp;
        // Clip-space clipping confines geometry to this integer-aligned viewport; the empty
        // border and shader tap clamping keep hardware PCF from reaching adjacent tiles.
        encoder.SetViewport(item.Tile.X + 1, item.Tile.Y + 1, item.Tile.Size - 2, item.Tile.Size - 2);
        encoder.SetBindGroup(1, self._jointGroup);
        var skinnedActive = (bool?)null;
        foreach (var (instance, primitive, _) in self._ctx.Opaque)
        {
            var skinned = primitive.Skinned && instance.JointOffset >= 0;
            if (skinnedActive != skinned)
            {
                encoder.SetPipeline(skinned ? self.SkinnedPipeline() : self._pipeline);
                skinnedActive = skinned;
            }
            // Budget guaranteed by the up-front check in Setup.
            var uniforms = new ShadowDrawUniformsGpu
            {
                LightMvp = instance.Model * vp,
                // The caster poses from the same palette slice its mesh does, so the shadow
                // tracks the animation instead of staying in bind pose.
                Params = new Vector4(skinned ? instance.JointOffset : 0f, 0f, 0f, 0f),
            };
            var slot = self._stagedDraws;
            MemoryMarshal.Write(self._staging.AsSpan(slot * (int)self._ctx.DrawStride), in uniforms);
            encoder.SetBindGroup(0, self._drawGroup, dynamicOffset: (uint)(slot * self._ctx.DrawStride));
            encoder.SetVertexBuffer(0, primitive.VertexBuffer, 0, primitive.VertexByteLength);
            encoder.SetIndexBuffer(primitive.IndexBuffer, IndexFormat.Uint32, 0, primitive.IndexByteLength);
            encoder.DrawIndexed(new DrawIndexedCommand(primitive.IndexCount, 1, 0, 0, 0));
            self._stagedDraws++;
        }
    }

    private PipelineHandle SkinnedPipeline()
    {
        if (_skinnedPipeline.IsValid) return _skinnedPipeline;
        var layout = _program.VertexBuffersByEntryPoint.TryGetValue("vertexMainSkinned", out var vb)
            ? vb
            : throw new InvalidOperationException("shadow.slang reflects no vertexMainSkinned layout.");
        _skinnedPipeline = _ctx.Renderer.CreateDepthOnlyPipeline(
            _program, TextureFormat.Depth32Float, layout, vertexEntryPoint: "vertexMainSkinned");
        return _skinnedPipeline;
    }

    // Perspective texel sizes are metres per metre of receiver distance.
    private static Matrix4x4 ComputeLocalLightMatrix(
        PbrLight light, int face, uint resolution, out float texelWorld)
    {
        switch (light.Type)
        {
            case PbrLightType.Spot:
            {
                var aim = light.Direction.LengthSquared() > 1e-6f ? Vector3.Normalize(-light.Direction) : -Vector3.UnitY;
                var up = MathF.Abs(aim.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
                var view = PbrMath.LookAt(light.Position, light.Position + aim, up);
                var fov = Math.Clamp(light.SpotOuterDegrees * (MathF.PI / 180f) * 1.05f, 0.1f, 3.0f);
                var proj = PbrMath.Perspective(fov, 1f, 0.05f, MathF.Max(light.Range, 1f));
                texelWorld = 2f * MathF.Tan(fov * 0.5f) / resolution; // per metre of distance
                return PbrMath.ViewProjection(view, proj);
            }
            case PbrLightType.Point:
            {
                var (dir, up) = CubeFace(face);
                var view = PbrMath.LookAt(light.Position, light.Position + dir, up);
                var proj = PbrMath.Perspective(MathF.PI / 2f, 1f, 0.05f, MathF.Max(light.Range, 1f));
                texelWorld = 2f / resolution; // tan(90°/2) = 1; per metre of distance
                return PbrMath.ViewProjection(view, proj);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(light), "Expected a point or spot light.");
        }
    }

    // Standard cube-face direction/up (RH), indexed to match the shader's getPointShadowFace:
    // +X, -X, +Y, -Y, +Z, -Z.
    private static (Vector3 Dir, Vector3 Up) CubeFace(int face) => face switch
    {
        0 => (Vector3.UnitX, -Vector3.UnitY),
        1 => (-Vector3.UnitX, -Vector3.UnitY),
        2 => (Vector3.UnitY, Vector3.UnitZ),
        3 => (-Vector3.UnitY, -Vector3.UnitZ),
        4 => (Vector3.UnitZ, -Vector3.UnitY),
        _ => (-Vector3.UnitZ, -Vector3.UnitY),
    };

    // World-space AABB over the opaque casters (their object-space bounds transformed by Model).
    private void ComputeWorldBounds(out Vector3 center, out Vector3 extent)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (instance, primitive, _) in _ctx.Opaque)
        {
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? primitive.LocalMin.X : primitive.LocalMax.X,
                    (c & 2) == 0 ? primitive.LocalMin.Y : primitive.LocalMax.Y,
                    (c & 4) == 0 ? primitive.LocalMin.Z : primitive.LocalMax.Z);
                var wp = Vector3.Transform(corner, instance.Model);
                min = Vector3.Min(min, wp);
                max = Vector3.Max(max, wp);
            }
        }
        if (min.X > max.X) { min = max = Vector3.Zero; }
        center = (min + max) * 0.5f;
        extent = (max - min) * 0.5f;
    }

    public void Dispose()
    {
        var renderer = _ctx.Renderer;
        if (_skinnedPipeline.IsValid) renderer.DestroyPipeline(_skinnedPipeline);
        renderer.DestroyPipeline(_pipeline);
        renderer.DestroyBindGroup(_drawGroup);
        renderer.DestroyBindGroup(_jointGroup);
        renderer.DestroyBuffer(_drawRing);
        renderer.DestroySampler(Sampler);
    }
}

/// <summary>One admitted shadow view. Split depths are positive camera-space distances.
/// The tile includes a one-texel clear guard; Vp maps into its inset viewport.</summary>
public readonly record struct ShadowView(int LightIndex, int Face, ShadowAtlasTile Tile, Matrix4x4 Vp,
    float TexelWorld, float SplitNear, float SplitFar, Vector2 DepthRange, bool Perspective);
