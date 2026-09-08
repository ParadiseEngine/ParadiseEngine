using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr;

/// <summary>The frame uniforms: what every lit draw reads through group 1, including the
/// description of the Forward+ froxel grid <see cref="LightCullingFeature"/> filled.</summary>
public sealed partial class SceneFeature
{
    // The frame UBO's CPU mirror lives in a FIELD, never in a local. FrameUniformsGpu is 49 KB (64
    // lights + 384 shadow views), and Mono's wasm interpreter aborts the ENTIRE runtime when it
    // tiers up a method whose locals exceed its frame budget: "Unable to run method
    // UploadFrameUniforms: locals size too big". The abort lands a couple of seconds into steady
    // rendering — long after the method has been interpreting happily, and long after any short
    // smoke test has reported success — so it reads as a random browser crash rather than a struct
    // size problem. Filling this in place also saves a 49 KB stack copy per frame on every backend.
    private FrameUniformsGpu _frameUniforms;

    // Test-only readback of the per-frame packed light array (e.g. to assert ShadowAtlas.X survives
    // the shadow-caster rebuild). Off by default so production frames never pay the array copy.
    internal bool CaptureFrameLightsForTest;
    private SceneLightArray _lastFrameLightsForTest;
    internal Vector4 GetLightShadowAtlasForTest(int lightIndex) => _lastFrameLightsForTest[lightIndex].ShadowAtlas;
    internal Vector4 GetLightSizeParamsForTest(int lightIndex) => _lastFrameLightsForTest[lightIndex].SizeParams;

    // World-space camera forward from the row-vector view matrix (third column is -forward).
    private static Vector3 CameraForward(in Matrix4x4 view) =>
        Vector3.Normalize(new Vector3(-view.M13, -view.M23, -view.M33));

    private void UploadFrameUniforms(PbrScene scene, bool contactActive)
    {
        // Filled IN PLACE through a ref to the field — never `var frame = new FrameUniformsGpu {…}`.
        // See _frameUniforms: a 49 KB local kills the wasm runtime at tier-up time.
        ref var frame = ref _frameUniforms;
        // The field persists between frames, so start from zero to keep the semantics of the fresh
        // struct this used to allocate. AmbientSh is the one that would actually bite: it is written
        // only when the scene carries SH coefficients, and its [0].w is the flag that switches the
        // shader onto the SH ambient path — a stale one keeps that path on after a scene drops it.
        frame = default;
        frame.Time = new Vector4(scene.ElapsedSeconds, 0f, 0f, 0f);
        frame.CameraPos = new Vector4(scene.Camera.Position, 0f);
        frame.Ambient = new Vector4(scene.Ambient.Sky, scene.Ambient.Exposure);
        frame.AmbientEquator = new Vector4(scene.Ambient.Equator, Math.Min(scene.Lights.Count, FrameUniformsGpu.MaxSceneLights));
        frame.AmbientGround = new Vector4(scene.Ambient.Ground, scene.Ambient.Flat ? 1f : 0f);
        // x: sky-reflection specular enabled (Godot reflected_light_source = Sky).
        frame.AaSettings = new Vector4(
            scene.HasSkyBackground && scene.SkyReflections ? 1f : 0f,
            _specularAaVariance, _specularAaClamp, 0f);
        // The froxel grid, as the fragment shader's cluster lookup needs it. Retracted to zero
        // while light culling is switched off: clusterParams.x < 1 is the shader's "test every
        // light" fallback, and without it a stale mask buffer would keep culling lights that
        // nothing is binning any more.
        frame.CameraForward = new Vector4(CameraForward(scene.Camera.View), _lightCulling.Near);
        frame.ClusterParams = _lightCulling.Active
            ? new Vector4(_lightCulling.TilesX, _lightCulling.TilesY, LightCullingFeature.ZSlices, _lightCulling.Far)
            : default;
        // x: 1/atlasSize. yzw: tone mapping — mode, exposure, white point.
        frame.ShadowSettings = new Vector4(
            1f / _shadows.AtlasSize,
            (float)scene.Tonemap.Mode,
            scene.Tonemap.Exposure,
            scene.Tonemap.White);
        frame.ShadowFilter = new Vector4(_shadows.BlurTexels, Math.Clamp(_shadows.CascadeBlend, 0, 0.3f), 0f, 0f);
        // Identity on a singular view-projection (degenerate camera) rather than NaN — the same
        // precedent as the sky's unprojection.
        frame.InvViewProj = Matrix4x4.Invert(_ctx.ViewProjection, out var invViewProj) ? invViewProj : Matrix4x4.Identity;
        // L2 sky-SH ambient: coefficients pass through verbatim; [0].w flags the SH path on.
        if (scene.Ambient.Sh is { Length: 9 } sh)
        {
            frame.AmbientSh[0] = new Vector4(sh[0], 1f);
            for (var i = 1; i < 9; i++)
            {
                frame.AmbientSh[i] = new Vector4(sh[i], 0f);
            }
        }
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            var source = scene.Lights[i];
            frame.Lights[i] = source.ToGpu();
            frame.Lights[i].SpotAngles.W = source.CastsShadows ? Math.Clamp(source.ShadowStrength, 0, 1) : 0;
        }

        // Compact view indices address matrices and tile metadata together; physical atlas
        // allocation can move without changing a light's identity or its cube-face order.
        for (var index = 0; index < _shadows.Views.Count; index++)
        {
            var view = _shadows.Views[index];
            var tile = view.Tile;
            var atlasSize = (float)_shadows.AtlasSize;
            frame.SceneLightShadowMatrices[index] = view.Vp;
            frame.ShadowViewRects[index] = new Vector4(tile.X + 1, tile.Y + 1, tile.Size - 2, tile.Size - 2) / atlasSize;
            frame.ShadowViewData[index] = new Vector4(view.TexelWorld, view.SplitNear, view.SplitFar, 0);
            frame.ShadowViewDepth[index] = new Vector4(view.DepthRange, view.Perspective ? 1 : 0, tile.Size - 2);
        }
        frame.ViewProj = _ctx.ViewProjection;
        var contact = scene.ContactShadows;
        frame.ContactShadowSettings = new Vector4(Math.Clamp(contact.Length, 0.001f, 10f),
            Math.Clamp(contact.Thickness, 0.001f, 1f), Math.Clamp(contact.Steps, 4, 64),
            contactActive ? Math.Clamp(contact.Strength, 0, 1) : 0);
        // Per-light shadow params: base view index (spotAngles.z), strength (spotAngles.w),
        // face count (shadowAtlas.y), soft-shadow flag (shadowAtlas.w) and shadow texel world
        // size (sizeParams.y — the bias scale, see ShadowFeature). shadowAtlas.x carries
        // the distance-attenuation decay, .z the LIGHT_PARAM_SPECULAR amount and sizeParams.x
        // the light's angular/world size (all set by ToGpu) and must be preserved here.
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            if (_shadows.FirstView(i) < 0) continue;
            var light = frame.Lights[i];
            light.SpotAngles.Z = _shadows.FirstView(i);
            light.SpotAngles.W = Math.Clamp(scene.Lights[i].ShadowStrength, 0f, 1f);
            light.ShadowAtlas = new Vector4(light.ShadowAtlas.X, _shadows.ViewCount(i), light.ShadowAtlas.Z, scene.Lights[i].SoftShadows ? 1f : 0f);
            light.SizeParams.Y = _shadows.TexelWorld(i);
            light.SizeParams.W = scene.Lights[i].Type == PbrLightType.Directional
                ? MathF.Tan(Math.Clamp(scene.Lights[i].ShadowAngularDiameter, 0, 45) * MathF.PI / 360f)
                : Math.Clamp(scene.Lights[i].ShadowSourceRadius, 0, 100f);
            frame.Lights[i] = light;
        }
        _ctx.Renderer.UpdateBuffer<FrameUniformsGpu>(_ctx.FrameUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref frame, 1));
        if (CaptureFrameLightsForTest) _lastFrameLightsForTest = frame.Lights;
    }

}
