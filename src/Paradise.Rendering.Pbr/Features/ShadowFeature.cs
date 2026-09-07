using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Shadow mapping: a Depth32Float 2D-array with one layer per shadow view, filled by
/// per-layer depth-only caster passes at <see cref="RenderPassEvent.Shadows"/> and sampled as a
/// depth array by the scene pass.
///
/// <para>Each frame plans one view per shadow-casting light face — directional and spot take one
/// layer, point takes six — and grows the array to fit. The plan is what the scene's frame
/// uniforms carry to the shader, so <see cref="SceneFeature"/> reads it after this feature's
/// setup has run.</para></summary>
public sealed class ShadowFeature : IRenderFeature
{
    private const uint DefaultMapSize = 1024;
    // One array layer per shadow view. Cap = every scene light casting a 6-face point shadow.
    private const int MaxLayers = FrameUniformsGpu.MaxSceneLights * 6; // 48

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
    private uint _layerCapacity;
    private int _stagedDraws;

    private readonly List<(int LightIndex, int Face, uint Layer, Matrix4x4 Vp)> _views = [];
    private readonly int[] _baseLayer = new int[FrameUniformsGpu.MaxSceneLights];
    private readonly int[] _faceCount = new int[FrameUniformsGpu.MaxSceneLights];
    // Shadow texel world size per light (see ComputeLightMatrix), uploaded as sizeParams.y so the
    // shader's normal-offset bias scales with the map's actual texel density.
    private readonly float[] _texelWorld = new float[FrameUniformsGpu.MaxSceneLights];

    internal ShadowFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;

        // Comparison sampler; clamp so a PCF tap near a layer edge reads that layer's border,
        // never wraps.
        Sampler = renderer.CreateSampler(new SamplerDesc(
            "PbrShadowSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest,
            MaxAnisotropy: 1, Compare: CompareFunction.LessEqual));

        // A valid array must always exist even when nothing casts, because the scene's frame
        // group binds it unconditionally; hence the minimum of one layer.
        EnsureArray(1);
        // Plan() is where "no light has a tile" is normally established, and a build with shadows
        // switched off never runs one — so the invariant is established here too, rather than
        // resting on _baseLayer's zeroes, which mean "layer 0" and not "no layer".
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

    /// <summary>Per-layer shadow map resolution, clamped to [256, 8192]. The array is re-declared
    /// at the new size on the next frame.</summary>
    public uint MapSize
    {
        get => _mapSize;
        set
        {
            var clamped = Math.Clamp(value, 256u, 8192u);
            if (clamped == _mapSize) return;
            _mapSize = clamped;
            _layerCapacity = 0;
        }
    }

    /// <summary>Soft-shadow PCF disk radius in shadow texels, clamped to [0.5, 8] — below ~2 the
    /// map's texel staircase shows through the 8-tap Vogel filter, far above it contact shadows
    /// detach into mush.</summary>
    public float BlurTexels
    {
        get => _blurTexels;
        set => _blurTexels = Math.Clamp(value, 0.5f, 8f);
    }

    /// <summary>Radius in world metres of the camera-centred area the directional shadow map
    /// covers; 0 falls back to the whole-scene fit. See <see cref="ComputeDirectionalLightMatrix"/>.</summary>
    public float DirectionalRadius { get; set; } = 50f;

    /// <summary>The comparison sampler the scene pass reads the array with.</summary>
    public SamplerHandle Sampler { get; }

    // The frame's shadow plan, valid after Setup until the next frame.
    internal IReadOnlyList<(int LightIndex, int Face, uint Layer, Matrix4x4 Vp)> Views => _views;
    internal int BaseLayer(int light) => _baseLayer[light];
    internal int FaceCount(int light) => _faceCount[light];
    internal float TexelWorld(int light) => _texelWorld[light];

    public void Resize(uint width, uint height)
    {
        // Shadow maps are sized by MapSize, not by the frame.
    }

    /// <summary>The plan outlives the frame that built it — the scene's frame uniforms are
    /// written from it — so a feature switched off mid-run has to retract it, or every light
    /// keeps sampling the layer it last owned out of an array nobody is filling any more.</summary>
    public void OnEnabledChanged(bool enabled)
    {
        if (!enabled) ClearPlan();
    }

    private void ClearPlan()
    {
        _views.Clear();
        Array.Fill(_baseLayer, -1);
        Array.Clear(_faceCount);
    }

    public void Setup(in FrameContext frame)
    {
        Plan(_ctx.Scene, _ctx.View);
        EnsureArray((uint)_views.Count);

        // Ring budget: views × casters. Hard-fail up front — like the main-pass check — so a
        // partial fill (silently missing shadows) cannot ship.
        var total = _views.Count * _ctx.Opaque.Count;
        if (total > PbrContext.MaxDrawsPerFrame)
            throw new InvalidOperationException(
                $"{total} shadow-caster draws ({_views.Count} views × {_ctx.Opaque.Count} casters) exceed the {PbrContext.MaxDrawsPerFrame}-slot shadow ring.");

        _stagedDraws = 0;
        var array = frame.Graph.Texture(PbrTargets.ShadowArray);
        for (var k = 0; k < _views.Count; k++)
        {
            frame.Graph.AddRasterPass("Shadow.Layer", RenderPassEvent.Shadows)
                .DepthLayer(array, _views[k].Layer, LoadOp.Clear, clear: 1f)
                .Record(this, RecordLayer, k);
        }
    }

    /// <summary>Upload the caster uniforms the recorders staged. After compile, because staging
    /// happens while recording.</summary>
    internal void UploadStagedDraws()
    {
        if (_stagedDraws > 0)
            _ctx.Renderer.UpdateBuffer<byte>(_drawRing, 0, _staging.AsSpan(0, _stagedDraws * (int)_ctx.DrawStride));
    }

    // Assign one array layer per shadow view to every shadow-casting light and compute each
    // face's light-space matrix, fit to the opaque casters' world AABB. When nothing casts the
    // plan is empty and no pass is declared (nothing samples an unwritten layer; base layer -1).
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
        var layerCount = 0;
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            var light = scene.Lights[i];
            if (!light.CastsShadows) continue;
            var faceCount = light.Type == PbrLightType.Point ? 6 : 1;
            if (layerCount + faceCount > MaxLayers) continue; // won't fit; a smaller later light still can
            _baseLayer[i] = layerCount;
            _faceCount[i] = faceCount;
            for (var f = 0; f < faceCount; f++)
            {
                _views.Add((i, f, (uint)(layerCount + f),
                    ComputeLightMatrix(light, f, center, extent, cameraPosition, out var texelWorld)));
                // Same for every cube face (the six 90° frusta share one texel density).
                _texelWorld[i] = texelWorld;
            }
            layerCount += faceCount;
        }
    }

    // Grow-only: a single shared texture across all shadow views keeps the frame group stable
    // between frames of equal (or smaller) shadow-layer count.
    private void EnsureArray(uint layerCount)
    {
        layerCount = Math.Max(1, layerCount);
        if (layerCount <= _layerCapacity) return;
        _ctx.Targets.Ensure(PbrTargets.ShadowArray, PbrTargets.RenderTarget(_mapSize, _mapSize, TextureFormat.Depth32Float, layerCount));
        _layerCapacity = layerCount;
    }

    // Depth-only fill of ONE layer. Every opaque caster is drawn with
    // lightMvp = model × faceViewProjection (mirrors the main Mvp = model × viewProjection so the
    // shadow shader matches pbr.slang). No viewport math — each layer owns the whole [0,1].
    private static void RecordLayer(ShadowFeature self, ref PassRecording pass, int view)
    {
        ref var encoder = ref pass.Encoder;
        var vp = self._views[view].Vp;
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

    // Light-space view-projection for one shadow face: directional = ortho fit to a camera-centred
    // circle (falling back to the scene AABB for small scenes); spot = perspective down the cone;
    // point = one of six 90°-FOV cube faces.
    //
    // texelWorld is the shadow texel's WORLD size, the quantity the shader scales its normal-offset
    // bias by (SceneLight.sizeParams.y): a fixed world-space bias is only ever tuned for one texel
    // size, and any coarser map (smaller texture, larger footprint) outgrows it and self-shadows in
    // diagonal bands. Ortho (directional) texels have one world size; perspective (spot/point)
    // texels grow with distance, so those report metres PER METRE of receiver distance and the
    // shader multiplies by its own distance to the light.
    private Matrix4x4 ComputeLightMatrix(
        PbrLight light, int face, Vector3 center, Vector3 extent, Vector3 cameraPosition,
        out float texelWorld)
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
                texelWorld = 2f * MathF.Tan(fov * 0.5f) / _mapSize; // per metre of distance
                return PbrMath.ViewProjection(view, proj);
            }
            case PbrLightType.Point:
            {
                var (dir, up) = CubeFace(face);
                var view = PbrMath.LookAt(light.Position, light.Position + dir, up);
                var proj = PbrMath.Perspective(MathF.PI / 2f, 1f, 0.05f, MathF.Max(light.Range, 1f));
                texelWorld = 2f / _mapSize; // tan(90°/2) = 1; per metre of distance
                return PbrMath.ViewProjection(view, proj);
            }
            default: // Directional
                return ComputeDirectionalLightMatrix(
                    light.Direction, center, extent, cameraPosition,
                    DirectionalRadius, _mapSize, out texelWorld);
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

    // Directional light view-projection (RH, clip-Z [0,1]).
    //
    // The XY footprint is a SQUARE of side 2·min(shadowRadius, sceneRadius), centred on the
    // camera (clamped into the scene AABB) — not the scene AABB itself. Fitting the whole scene
    // is what made a growing world quietly destroy its own shadow quality: texel size scales with
    // the AABB, blurring every shadow edge (the acne this once caused is gone — the bias now
    // scales with texelWorld — but the resolution loss is inherent).
    // A camera-centred fit keeps metres-per-texel constant forever. Two details carry it:
    //
    // * The centre is SNAPPED to whole shadow texels in the light's plane basis, so the box
    //   translates in texel steps as the camera glides and shadow edges do not shimmer. The basis
    //   is derived from the light direction alone, so it is stable frame to frame.
    // * The DEPTH range still spans the scene AABB along the light, so a tall caster outside the
    //   circle (a skyline tower, the highway deck) still lays its shadow across it.
    //
    // When the scene fits inside the radius anyway (or the radius is disabled with <= 0), the
    // legacy whole-AABB fit applies — a small scene keeps its tighter, non-square box.
    private static Matrix4x4 ComputeDirectionalLightMatrix(
        Vector3 surfaceToLight, Vector3 center, Vector3 extent, Vector3 cameraPosition,
        float shadowRadius, uint shadowMapSize, out float texelWorld)
    {
        var lightDir = surfaceToLight.LengthSquared() > 1e-6f ? Vector3.Normalize(surfaceToLight) : Vector3.UnitY;
        const float depthPad = 32f;
        const float xyPad = 1f;
        var sceneRadius = MathF.Max(4f, 0.5f * extent.Length());
        var up = MathF.Abs(lightDir.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;

        if (shadowRadius > 0f && shadowRadius < sceneRadius)
        {
            var radius = shadowRadius + xyPad;
            var focus = Vector3.Clamp(cameraPosition, center - extent, center + extent);

            // Snap the focus to the shadow-texel grid in the light's own plane basis.
            var right = Vector3.Normalize(Vector3.Cross(up, lightDir));
            var planeUp = Vector3.Cross(lightDir, right);
            var texel = 2f * radius / shadowMapSize;
            var focusRight = Vector3.Dot(focus, right);
            var focusUp = Vector3.Dot(focus, planeUp);
            focus += right * (MathF.Floor(focusRight / texel) * texel - focusRight)
                   + planeUp * (MathF.Floor(focusUp / texel) * texel - focusUp);

            var eye = focus + lightDir * (sceneRadius + depthPad);
            var lightView = PbrMath.LookAt(eye, focus, up);

            // Depth range from the scene AABB so out-of-circle casters still cast in.
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (var c = 0; c < 8; c++)
            {
                var corner = center + new Vector3(
                    (c & 1) == 0 ? -extent.X : extent.X,
                    (c & 2) == 0 ? -extent.Y : extent.Y,
                    (c & 4) == 0 ? -extent.Z : extent.Z);
                var lz = Vector3.Transform(corner, lightView).Z;
                minZ = MathF.Min(minZ, lz); maxZ = MathF.Max(maxZ, lz);
            }
            // RH light space: the scene sits at negative Z. near/far are positive distances.
            var nearPlane = MathF.Max(0.01f, -maxZ - depthPad);
            var farPlane = MathF.Max(nearPlane + 1f, -minZ + depthPad);
            var proj = PbrMath.OrthographicOffCenter(-radius, radius, -radius, radius, nearPlane, farPlane);
            texelWorld = texel; // the snap grid IS the texel size: 2·radius / mapSize
            return PbrMath.ViewProjection(lightView, proj);
        }

        // Legacy whole-scene fit. Matches bank-heist (up-vector guard, XY/Z padding).
        {
            var eye = center + lightDir * (sceneRadius + depthPad);
            var lightView = PbrMath.LookAt(eye, center, up);

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (var c = 0; c < 8; c++)
            {
                var corner = center + new Vector3(
                    (c & 1) == 0 ? -extent.X : extent.X,
                    (c & 2) == 0 ? -extent.Y : extent.Y,
                    (c & 4) == 0 ? -extent.Z : extent.Z);
                var lp = Vector3.Transform(corner, lightView);
                minX = MathF.Min(minX, lp.X); maxX = MathF.Max(maxX, lp.X);
                minY = MathF.Min(minY, lp.Y); maxY = MathF.Max(maxY, lp.Y);
                minZ = MathF.Min(minZ, lp.Z); maxZ = MathF.Max(maxZ, lp.Z);
            }
            // RH light space: the scene sits at negative Z. near/far are positive distances.
            var nearPlane = MathF.Max(0.01f, -maxZ - depthPad);
            var farPlane = MathF.Max(nearPlane + 1f, -minZ + depthPad);
            var proj = PbrMath.OrthographicOffCenter(
                minX - xyPad, maxX + xyPad, minY - xyPad, maxY + xyPad, nearPlane, farPlane);
            // The map is square but the fit is not; the wider axis has the coarser texels, and the
            // bias must cover the worst case.
            texelWorld = MathF.Max(maxX - minX + 2f * xyPad, maxY - minY + 2f * xyPad) / shadowMapSize;
            return PbrMath.ViewProjection(lightView, proj);
        }
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
