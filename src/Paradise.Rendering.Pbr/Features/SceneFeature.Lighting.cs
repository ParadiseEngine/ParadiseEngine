using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr;

/// <summary>The frame uniforms: what every lit draw reads through group 1, including the
/// description of the Forward+ froxel grid <see cref="LightCullingFeature"/> filled.</summary>
public sealed partial class SceneFeature
{
    // Keep this 31 KB uniform struct in a field: large locals exceed Mono wasm tier-up limits and
    // abort the runtime. Filling it in place also avoids a stack copy.
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

    private void UploadFrameUniforms(PbrScene scene)
    {
        // Filled IN PLACE through a ref to the field — never `var frame = new FrameUniformsGpu {…}`.
        // See _frameUniforms: a 31 KB local kills the wasm runtime at tier-up time.
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
        frame.CameraForward = new Vector4(CameraForward(_ctx.View), _lightCulling.Near);
        frame.ClusterParams = _lightCulling.Active
            ? new Vector4(_lightCulling.TilesX, _lightCulling.TilesY, LightCullingFeature.ZSlices, _lightCulling.Far)
            : default;
        // x: 1/shadowMapSize (per-layer texel). yzw: tone mapping — mode, exposure, white point.
        frame.ShadowSettings = new Vector4(
            1f / _shadows.MapSize,
            (float)scene.Tonemap.Mode,
            scene.Tonemap.Exposure,
            scene.Tonemap.White);
        frame.ShadowFilter = new Vector4(_shadows.BlurTexels, 0f, 0f, 0f);
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
            frame.Lights[i] = scene.Lights[i].ToGpu();
        }

        // Per-view light-space matrices from the shadow plan, keyed by the view's own ARRAY LAYER.
        // Keyed by lightIndex * 6 once, which reserved six matrix slots for every light whether or
        // not it cast anything and tied the matrix budget to the light budget; the layer is the
        // index the shader already computes to sample the map, so the two now agree by
        // construction rather than by arithmetic that has to match.
        foreach (var (_, _, layer, vp) in _shadows.Views)
        {
            frame.SceneLightShadowMatrices[(int)layer] = vp;
        }
        // Update shadow layer/strength, face count/soft flag and texel scale. Preserve attenuation
        // in shadowAtlas.x, specular in .z and light size in sizeParams.x.
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            if (_shadows.BaseLayer(i) < 0) continue;
            var light = frame.Lights[i];
            light.SpotAngles.Z = _shadows.BaseLayer(i);
            light.SpotAngles.W = Math.Clamp(scene.Lights[i].ShadowStrength, 0f, 1f);
            light.ShadowAtlas = new Vector4(light.ShadowAtlas.X, _shadows.FaceCount(i), light.ShadowAtlas.Z, scene.Lights[i].SoftShadows ? 1f : 0f);
            light.SizeParams.Y = _shadows.TexelWorld(i);
            frame.Lights[i] = light;
        }
        _ctx.Renderer.UpdateBuffer<FrameUniformsGpu>(_ctx.FrameUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref frame, 1));
        if (CaptureFrameLightsForTest) _lastFrameLightsForTest = frame.Lights;
    }

}
