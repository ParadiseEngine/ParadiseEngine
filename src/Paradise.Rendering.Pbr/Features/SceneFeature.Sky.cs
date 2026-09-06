using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>The environment: the gradient-sky background drawn first in the scene pass, the
/// sky-reflection specular LUT it is prefiltered into, and the environment-BRDF table.</summary>
public sealed partial class SceneFeature
{
    // Gradient-sky background: a fullscreen triangle (no vertex buffer) drawn first in the main pass
    // with depth-write off / compare Always, so geometry overdraws it. Colours come from a tiny UBO.
    private PipelineHandle _skyPipeline;
    private BufferHandle _skyUniformBuffer;
    private BindGroupHandle _skyGroup;
    // Sky-reflection specular (Godot reflected_light_source = Sky): the gradient sky GGX-prefiltered
    // on the CPU into a small LUT (u: reflection.y, v: roughness — the gradient is azimuth-symmetric).
    // Rgba8UnormSrgb: radiance ∈ [0,1] on the standard hardware-decoded color path. Rebaked only
    // when the sky colours/curves change; group 3 binds it alongside the SSAO resources.
    private TextureHandle _skySpecLutTexture;
    private TextureViewHandle _skySpecLutView;
    private SamplerHandle _skySpecSampler;
    private (Vector3, Vector3, Vector3, Vector3, float, float, Vector3, float, float, float)? _skySpecKey;
    // The environment-BRDF (DFG) table: the real GGX pre-integral, baked once at startup —
    // Godot's integrate_dfg.glsl integrand (Schlick-GGX, IBL k = α²/2, 1024 Hammersley samples).
    private TextureHandle _dfgLutTexture;
    private TextureViewHandle _dfgLutView;

    private const int DfgLutSize = 128;
    private const int SkySpecLutWidth = 64;
    private const int SkySpecLutHeight = 16; // rows 0..7 gradient half, 8..15 sun half
    internal const float SkySpecSunScale = 4f; // HDR headroom for the sun half in 8-bit sRGB texels

    private void CreateSky()
    {
        var renderer = _ctx.Renderer;
        // Fullscreen triangle (the vertex shader uses SV_VertexID), depth-write off + compare
        // Always so it never occludes or is occluded by scene geometry. Always the linear entry:
        // the sRGB decision lives in the composite.
        var skyProgram = ShaderPrograms.Load("Shaders.sky");
        _skyPipeline = renderer.CreatePipeline(
            skyProgram, PbrTargets.HdrFormat,
            depthStencilFormat: TextureFormat.Depth32Float,
            depthWriteEnabled: false,
            depthCompare: CompareFunction.Always,
            fragmentEntryPoint: "skyFragment");
        var skyUniformDesc = new BufferDesc("PbrSkyUniforms", (ulong)Unsafe.SizeOf<SkyUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst);
        _skyUniformBuffer = renderer.CreateBuffer(in skyUniformDesc);
        _skyGroup = renderer.CreateBindGroup(new BindGroupDesc("PbrSkyGroup", ShaderPrograms.FindGroup(skyProgram, 0), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _skyUniformBuffer, 0, (ulong)Unsafe.SizeOf<SkyUniformsGpu>()),
        }));
    }

    private void CreateLuts()
    {
        var renderer = _ctx.Renderer;
        _skySpecLutTexture = renderer.CreateTexture(new TextureDesc(
            "PbrSkySpecularLut", SkySpecLutWidth, SkySpecLutHeight, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba8UnormSrgb, TextureUsage.TextureBinding | TextureUsage.CopyDst));
        _skySpecLutView = renderer.CreateTextureView(new TextureViewDesc(
            "PbrSkySpecularLutView", _skySpecLutTexture, TextureViewDimension.D2, 0, 1));
        _skySpecSampler = renderer.CreateSampler(new SamplerDesc(
            "PbrSkySpecularSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest));
        // The LUT starts black (no reflections until a sky is provided); baked on first use.
        renderer.WriteTexture(_skySpecLutTexture, 0, new byte[SkySpecLutWidth * SkySpecLutHeight * 4],
            SkySpecLutWidth * 4, SkySpecLutHeight, SkySpecLutWidth, SkySpecLutHeight);
        _dfgLutTexture = renderer.CreateTexture(new TextureDesc(
            "PbrDfgLut", DfgLutSize, DfgLutSize, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba16Float, TextureUsage.TextureBinding | TextureUsage.CopyDst));
        _dfgLutView = renderer.CreateTextureView(new TextureViewDesc(
            "PbrDfgLutView", _dfgLutTexture, TextureViewDimension.D2, 0, 1));
        BakeDfgLut();
    }

    private void UploadSky(PbrScene scene)
    {
        if (scene.SkyReflections) EnsureSkySpecularLut(scene);
        // Inverse of the same view-projection used for MVP, so the sky shader can unproject each
        // background pixel's NDC to a world-space eye ray (uploaded raw-bytes, like MVP). Falls
        // back to identity on a singular VP (degenerate camera) rather than uploading NaN — same
        // precedent as PbrMath.NormalMatrix/TryScreenPointToRay.
        if (!Matrix4x4.Invert(_ctx.ViewProjection, out var invViewProj))
            invViewProj = Matrix4x4.Identity;
        var skyUniforms = new SkyUniformsGpu
        {
            // skyTop.w / skyHorizon.w carry the sun halo thresholds (see sky.slang).
            SkyTop = new Vector4(scene.SkyTopColor, scene.SkySunAngleMaxCos),
            SkyHorizon = new Vector4(scene.SkyHorizonColor, scene.SkySunInvCurve),
            GroundBottom = new Vector4(scene.SkyGroundBottom, 1f),
            GroundHorizon = new Vector4(scene.SkyGroundHorizon, 1f),
            // zw + CameraPos.w carry the tone operator so the sky shader can blend the LINEAR
            // gradient first and tonemap per-pixel (Godot's order; see sky.slang header).
            Params = new Vector4(scene.SkySkyCurveInv, scene.SkyGroundCurveInv, (float)scene.Tonemap.Mode, scene.Tonemap.Exposure),
            CameraPos = new Vector4(scene.Camera.Position, scene.Tonemap.White),
            SunDirection = new Vector4(scene.SkySunDirection, scene.SkySunEnabled ? 1f : 0f),
            SunColor = new Vector4(scene.SkySunColorEnergy, scene.SkySunSizeCos),
            InvViewProj = invViewProj,
        };
        _ctx.Renderer.UpdateBuffer<SkyUniformsGpu>(_skyUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref skyUniforms, 1));
    }

    // Gradient-sky background first (fullscreen, no depth write) so geometry draws over it.
    private void RecordSky(ref PassRecording pass)
    {
        if (!_ctx.Scene.HasSkyBackground) return;
        pass.Encoder.SetPipeline(_skyPipeline);
        pass.Encoder.SetBindGroup(0, _skyGroup);
        pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
    }

    /// <summary>Bake the environment-BRDF (DFG) table: an exact port of Godot's
    /// integrate_dfg.glsl (GGX importance sampling, Schlick-GGX with the IBL k = α²/2,
    /// 1024 Hammersley samples). Stored as (scale = ∫(1−Fc)·G_Vis, bias = ∫Fc·G_Vis) so the
    /// shader computes specular = F0·scale + f90·bias and the multiscatter energy compensation
    /// uses scale + bias; u = NdotV, v = roughness. ~130 ms once at startup.</summary>
    private void BakeDfgLut()
    {
        const int samples = 1024;
        var data = new byte[DfgLutSize * DfgLutSize * 8]; // 4 × 16-bit half channels
        for (var row = 0; row < DfgLutSize; row++)
        {
            var roughness = (row + 0.5f) / DfgLutSize;
            var alpha2 = roughness * roughness * roughness * roughness;
            var k = roughness * roughness / 2f; // Schlick-GGX IBL k
            for (var col = 0; col < DfgLutSize; col++)
            {
                var ndv = (col + 0.5f) / DfgLutSize;
                var v = new Vector3(MathF.Sqrt(1f - ndv * ndv), 0f, ndv); // N = +Z tangent frame
                float a = 0f, b = 0f;
                for (var i = 0; i < samples; i++)
                {
                    var u1 = (float)i / samples;
                    var u2 = RadicalInverse((uint)i);
                    var phi = 2f * MathF.PI * u1;
                    var cosTheta = MathF.Sqrt((1f - u2) / (1f + (alpha2 - 1f) * u2));
                    var sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
                    var h = new Vector3(MathF.Cos(phi) * sinTheta, MathF.Sin(phi) * sinTheta, cosTheta);
                    var l = 2f * Vector3.Dot(v, h) * h - v;
                    var ndl = Math.Clamp(l.Z, 0f, 1f);
                    if (ndl <= 0f) continue;
                    var ndh = Math.Clamp(h.Z, 0f, 1f);
                    var vdh = Math.Clamp(Vector3.Dot(v, h), 0f, 1f);
                    var g = (ndv / (ndv * (1f - k) + k)) * (ndl / (ndl * (1f - k) + k));
                    var gVis = g * vdh / MathF.Max(ndh * ndv, 1e-6f);
                    var fc = MathF.Pow(1f - vdh, 5f);
                    a += fc * gVis;
                    b += gVis;
                }
                a /= samples;
                b /= samples;
                var idx = (row * DfgLutSize + col) * 8;
                WriteHalf(data, idx + 0, b - a); // r = scale (∫(1−Fc)·G_Vis)
                WriteHalf(data, idx + 2, a);     // g = bias  (∫Fc·G_Vis)
                WriteHalf(data, idx + 4, 0f);
                WriteHalf(data, idx + 6, 1f);
            }
        }
        _ctx.Renderer.WriteTexture(_dfgLutTexture, 0, data, DfgLutSize * 8, DfgLutSize, DfgLutSize, DfgLutSize);

        static void WriteHalf(byte[] dest, int offset, float value)
        {
            var bits = BitConverter.HalfToUInt16Bits((Half)value);
            dest[offset] = (byte)bits;
            dest[offset + 1] = (byte)(bits >> 8);
        }
    }

    // Van der Corput radical inverse: the second Hammersley coordinate.
    private static float RadicalInverse(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10f;
    }

    // GGX-prefilter the sky into the specular LUT (split-sum first term, N=V=R convention),
    // split into two row halves:
    //   rows 0..7  — the GRADIENT (azimuth-symmetric → depends only on reflection.y = u).
    //   rows 8..15 — the SUN disk/halo, which is radially symmetric around the sun direction →
    //                depends only on dot(reflection, sunDir) = u. Exact, no cubemap needed.
    // The sun half is stored ÷SkySpecSunScale for HDR headroom in the 8-bit sRGB texel (the
    // disk radiance is colour × energy, typically > 1); the shader multiplies it back.
    // CPU cost ~64×16×64 evaluations, re-run only when the sky or sun changes.
    private void EnsureSkySpecularLut(PbrScene scene)
    {
        var key = (scene.SkyTopColor, scene.SkyHorizonColor, scene.SkyGroundBottom, scene.SkyGroundHorizon,
            scene.SkySkyCurveInv, scene.SkyGroundCurveInv,
            scene.SkySunEnabled ? scene.SkySunColorEnergy : Vector3.Zero,
            scene.SkySunSizeCos, scene.SkySunAngleMaxCos, scene.SkySunInvCurve);
        if (_skySpecKey == key) return;
        _skySpecKey = key;

        Vector3 Radiance(float y)
        {
            y = Math.Clamp(y, -1f, 1f);
            return y >= 0f
                ? Vector3.Lerp(scene.SkyTopColor, scene.SkyHorizonColor,
                    Math.Clamp(MathF.Pow(1f - y, scene.SkySkyCurveInv), 0f, 1f))
                : Vector3.Lerp(scene.SkyGroundBottom, scene.SkyGroundHorizon,
                    Math.Clamp(MathF.Pow(1f + y, scene.SkyGroundCurveInv), 0f, 1f));
        }

        // Godot's sun disk/halo weight (sky_material.cpp) as a function of the cosine to the sun.
        Vector3 SunRadiance(float cosToSun)
        {
            if (!scene.SkySunEnabled) return Vector3.Zero;
            float w;
            if (cosToSun > scene.SkySunSizeCos) w = 1f;
            else if (cosToSun > scene.SkySunAngleMaxCos)
            {
                float c2 = (scene.SkySunSizeCos - cosToSun) / (scene.SkySunSizeCos - scene.SkySunAngleMaxCos);
                w = Math.Clamp(MathF.Pow(1f - c2, scene.SkySunInvCurve), 0f, 1f);
            }
            else return Vector3.Zero;
            return scene.SkySunColorEnergy * (w / SkySpecSunScale);
        }

        static float SrgbEncode(float c)
        {
            c = Math.Clamp(c, 0f, 1f);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        }

        const int samples = 64;
        const int halfRows = SkySpecLutHeight / 2;
        var data = new byte[SkySpecLutWidth * SkySpecLutHeight * 4];
        for (var row = 0; row < SkySpecLutHeight; row++)
        {
            bool sunHalf = row >= halfRows;
            float roughness = (row % halfRows + 0.5f) / halfRows;
            float alpha = roughness * roughness;
            float alpha2 = alpha * alpha;
            for (var col = 0; col < SkySpecLutWidth; col++)
            {
                // For the gradient half, u = reflection.y; for the sun half, u = cos(angle to
                // sun) — either way the radiance is symmetric around the Y axis of this frame.
                float ry = col / (SkySpecLutWidth - 1f) * 2f - 1f;
                var r = new Vector3(MathF.Sqrt(MathF.Max(0f, 1f - ry * ry)), ry, 0f);
                // Tangent frame around R for GGX importance sampling.
                var up = MathF.Abs(r.Y) > 0.999f ? Vector3.UnitX : Vector3.UnitY;
                var tangent = Vector3.Normalize(Vector3.Cross(up, r));
                var bitangent = Vector3.Cross(r, tangent);
                Vector3 acc = default;
                float wSum = 0f;
                for (var s = 0; s < samples; s++)
                {
                    // Hammersley: (i+0.5)/N and the radical inverse of i.
                    float u1 = (s + 0.5f) / samples;
                    float u2 = RadicalInverse((uint)s);
                    float phi = 2f * MathF.PI * u1;
                    float cosTheta = MathF.Sqrt((1f - u2) / (1f + (alpha2 - 1f) * u2));
                    float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
                    var h = tangent * (MathF.Cos(phi) * sinTheta)
                        + bitangent * (MathF.Sin(phi) * sinTheta)
                        + r * cosTheta;
                    var l = 2f * Vector3.Dot(r, h) * h - r;
                    float ndl = Vector3.Dot(r, l);
                    if (ndl <= 0f) continue;
                    acc += (sunHalf ? SunRadiance(l.Y) : Radiance(l.Y)) * ndl;
                    wSum += ndl;
                }
                var c = wSum > 0f ? acc / wSum : (sunHalf ? SunRadiance(ry) : Radiance(ry));
                var i = (row * SkySpecLutWidth + col) * 4;
                data[i + 0] = (byte)MathF.Round(SrgbEncode(c.X) * 255f);
                data[i + 1] = (byte)MathF.Round(SrgbEncode(c.Y) * 255f);
                data[i + 2] = (byte)MathF.Round(SrgbEncode(c.Z) * 255f);
                data[i + 3] = 255;
            }
        }
        _ctx.Renderer.WriteTexture(_skySpecLutTexture, 0, data,
            SkySpecLutWidth * 4, SkySpecLutHeight, SkySpecLutWidth, SkySpecLutHeight);
    }

    private void DisposeSky()
    {
        var renderer = _ctx.Renderer;
        renderer.DestroyPipeline(_skyPipeline);
        renderer.DestroyBindGroup(_skyGroup);
        renderer.DestroyBuffer(_skyUniformBuffer);
    }

    private void DisposeLuts()
    {
        var renderer = _ctx.Renderer;
        renderer.DestroyTextureView(_skySpecLutView);
        renderer.DestroyTexture(_skySpecLutTexture);
        renderer.DestroySampler(_skySpecSampler);
        renderer.DestroyTextureView(_dfgLutView);
        renderer.DestroyTexture(_dfgLutTexture);
    }
}
