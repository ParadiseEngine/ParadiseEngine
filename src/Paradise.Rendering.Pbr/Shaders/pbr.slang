// pbr.slang — PBR metallic-roughness shader (Cook-Torrance BRDF), ported from bank-heist with
// skinning and shadow mapping deleted (bindings (0,3), (1,1), (2,1) freed for future PRs) and
// two properly-authored fragment entry points instead of runtime WGSL surgery:
//   fragmentMain     — linear output, for *UnormSrgb swapchain formats (hardware encodes)
//   fragmentMainSrgb — applies the sRGB OETF in-shader, for non-sRGB-only surfaces
// Implements GGX/Trowbridge-Reitz NDF, Smith-Schlick geometry, Schlick Fresnel, hemisphere
// ambient (no IBL), 1..8 punctual lights (directional/point/spot), geometric specular AA,
// BC5 two-channel normal reconstruction, and the KHR_materials_transmission glass response.
// The baseColor UV transform (KHR_texture_transform) lives in MaterialUniforms.

static const float PI = 3.14159265359;
static const int MaxSceneLights = 64;

struct SceneLight
{
    float4 positionAndType;       // xyz: position, w: 0 directional / 1 point / 2 spot
    float4 directionAndRange;     // xyz: direction from surface to light, w: range
    float4 colorAndIntensity;     // rgb: color, w: intensity
    float4 spotAngles;            // x: outer angle degrees, y: inner angle degrees, z: base shadow-array layer (<0 = no shadow), w: shadow strength (0 = none)
    float4 shadowAtlas;           // x: distance-attenuation decay, y: face count (1 = dir/spot, 6 = point), z: LIGHT_PARAM_SPECULAR, w: soft-shadow flag
    float4 sizeParams;            // x: LIGHT_PARAM_SIZE (directional: precomputed 1−cos(angular); point/spot: world radius), yzw unused
};

struct DrawUniforms
{
    float4x4 mvp;          // Model-View-Projection matrix
    float4x4 model;        // Model matrix (for world-space normals/positions)
    float4x4 normalMatrix; // Inverse-transpose model matrix (for world-space normals)
    float4 highlight;      // x: highlight weight, yzw unused
};

struct FrameUniforms
{
    float4   cameraPos;      // World-space camera position (xyz), w unused
    float4   ambient;        // Ambient sky color (rgb) + exposure multiplier (a)
    float4   ambientEquator; // Ambient equator color (rgb), w: scene light count
    float4   ambientGround;  // Ambient ground color (rgb), w: flat ambient flag
    float4   aaSettings;     // x: unused, y: specularAA variance, z: specularAA clamp, w: unused
    // L2 spherical-harmonic sky irradiance (E/π), 9 RGB coefficients in Ramamoorthi order
    // (Y00, Y1-1, Y10, Y11, Y2-2, Y2-1, Y20, Y21, Y22) with the band factors Â=(1, 2/3, 1/4)
    // premultiplied at export — the per-normal ambient Godot's sky-SH produces, replacing the
    // 3-zone hemisphere blend. ambientSh[0].w > 0.5 enables the SH path (zones are the fallback).
    float4   ambientSh[9];
    // Forward+ clustering (Godot's froxel model, CPU-binned): the screen is tiled 32x32 px and
    // view depth is split into logarithmic slices; clusterMasks holds TWO uint bitmask words per
    // froxel (word i>>5, bit i&31 = sceneLights[i] overlaps it). Directional lights are never
    // clustered.
    float4   cameraForward;  // xyz: world-space camera forward (unit), w: cluster near
    float4   clusterParams;  // x: tilesX, y: tilesY, z: zSlices, w: cluster far (x<1 disables)
    SceneLight sceneLights[MaxSceneLights];
    float4   shadowSettings; // x: 1/shadowMapSize (per-layer texel size), y: tonemap mode, z: exposure, w: white
    float4x4 sceneLightShadowMatrices[MaxSceneLights * 6]; // per light, per cube face (face 0 for dir/spot)
    float4   time;           // x: elapsed seconds (procedural material animation), yzw unused
};

struct MaterialUniforms
{
    float4 baseColorFactor;   // glTF pbrMetallicRoughness.baseColorFactor (rgba)
    float  metallicFactor;    // glTF pbrMetallicRoughness.metallicFactor
    float  roughnessFactor;   // glTF pbrMetallicRoughness.roughnessFactor
    float  normalScale;       // glTF normalTexture.scale
    float  occlusionStrength; // glTF occlusionTexture.strength
    float4 emissiveFactor;    // glTF emissiveFactor (rgb), w: KHR_materials_transmission factor
    float4 uvOffsetScale;     // KHR_texture_transform (baseColor): xy offset, zw scale
    float4 uvRotation;        // x: rotation radians, yzw unused
    // Procedural material (see the recipe library below): a noise-driven, time-animated surface
    // overriding albedo/roughness/metallic/emissive when kind >= 1. Object-space so the pattern
    // rides a rolling ball. procColorA/B tint the tintable recipes.
    float4 procColorA;        // recipe color A (rgb)
    float4 procColorB;        // recipe color B (rgb)
    float4 procParams;        // x: kind (0 = off), y: noise scale, z: flow speed, w: emissive strength
};

[[vk::binding(0, 0)]]
ConstantBuffer<DrawUniforms> draw;

[[vk::binding(0, 1)]]
ConstantBuffer<FrameUniforms> frame;

// Shadow map array (a single Depth32Float 2D array, one layer per shadow view) + comparison
// sampler, on the group-1 slots reserved when shadow mapping was removed. NOTE: the generated WGSL
// declares shadowTexture as texture_2d_array<f32>; a build-time patch (Slang.targets) rewrites it
// to texture_depth_2d_array, which WGSL requires for textureSampleCompareLevel (Slang issue #6942).
// The loader forces the matching bind-group layout (DepthTextureArray + ComparisonSampler) by these
// exact names.
[[vk::binding(1, 1)]]
Texture2DArray<float> shadowTexture;

[[vk::binding(2, 1)]]
SamplerComparisonState shadowSampler;

// Per-froxel light bitmasks (Forward+). Rebuilt by the renderer every frame; conservatively
// binned on the CPU (sphere bounds, ±1 froxel padding), so a set bit may be a false positive
// (the light's attenuation then contributes ~0) but never a false negative — clustering is
// pure culling and must not change pixels.
[[vk::binding(3, 1)]]
StructuredBuffer<uint> clusterMasks;

[[vk::binding(0, 2)]]
ConstantBuffer<MaterialUniforms> material;

[[vk::binding(1, 2)]]
Texture2D<float4> baseColorTexture;

[[vk::binding(2, 2)]]
SamplerState baseColorSampler;

[[vk::binding(3, 2)]]
Texture2D<float4> metallicRoughnessTexture;

[[vk::binding(4, 2)]]
Texture2D<float4> normalTexture;

[[vk::binding(5, 2)]]
Texture2D<float4> occlusionTexture;

[[vk::binding(6, 2)]]
Texture2D<float4> emissiveTexture;

// Group 3: screen-space ambient occlusion. positionTexture is the world-position pre-pass (xyz =
// world position, w = coverage; Rgba32Float, sampled with textureLoad — unfilterable, no sampler).
// AO is computed inline in the fragment and multiplies only the ambient term; intensity 0 disables
// it (the pre-pass is skipped and the shader early-outs, so the stale texture is never read).
struct SsaoUniforms
{
    float4 params; // x: intensity (0 = off), y: radius (world units), z: bias, w: power
    float4 screen; // xy: 1/screenSize (texel), zw: screenSize (pixels)
};

[[vk::binding(0, 3)]]
ConstantBuffer<SsaoUniforms> ssao;

[[vk::binding(1, 3)]]
Texture2D<float4> positionTexture;

// Sky-reflection specular (Godot Environment.reflected_light_source = Sky/Background): the
// ProceduralSky radiance GGX-prefiltered on the CPU into a small LUT. The gradient sky is
// azimuth-symmetric, so the prefiltered radiance depends only on reflection.y (u) and
// roughness (v). Enabled via frame.aaSettings.x (sun disk/halo excluded — a 1D-symmetric LUT
// cannot carry a directional glint; its diffuse-scale contribution is negligible).
[[vk::binding(2, 3)]]
Texture2D<float4> skySpecularLut;

[[vk::binding(3, 3)]]
SamplerState skySpecularSampler;

// The environment BRDF (DFG) table — the real GGX pre-integral (Godot's integrate_dfg.glsl
// integrand: Schlick-GGX with the IBL k = α²/2, 1024 Hammersley samples), CPU-baked at startup
// into a 128×128 Rgba16Float. r = scale (∫(1−Fc)·G_Vis), g = bias (∫Fc·G_Vis); specular =
// F0·scale + f90·bias, and dfg_y (the F0=1 response used by the multiscatter energy
// compensation) = scale + bias. u = NdotV, v = roughness. Replaces the Karis analytic fit,
// whose few-percent corner error showed up on metals (× energy compensation × many lights).
[[vk::binding(4, 3)]]
Texture2D<float4> dfgLut;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal   : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent  : TANGENT;
};

struct VertexOutput
{
    float4 position    : SV_Position;
    float3 worldPos    : WORLDPOS;
    float3 worldNormal : NORMAL;
    float2 texcoord    : TEXCOORD0;
    float4 worldTangent : TANGENT;
    float3 objectPos   : OBJECTPOS; // object-space position (procedural materials ride the mesh)
};

struct FragmentInput
{
    float4 position    : SV_Position;
    float3 worldPos    : WORLDPOS;
    float3 worldNormal : NORMAL;
    float2 texcoord    : TEXCOORD0;
    float4 worldTangent : TANGENT;
    float3 objectPos   : OBJECTPOS;
    // Godot flips the geometric normal on back-facing fragments (double-sided materials render
    // their inside with the outward normal negated); without this the inside of a mesh reads the
    // wrong ambient hemisphere / NdotL. The pipeline doesn't cull, so backfaces are shaded here.
    bool isFrontFace   : SV_IsFrontFace;
};

// --- PBR helper functions ---

// GGX/Trowbridge-Reitz NDF from a precomputed (possibly size_A-widened) NdotH.
float distributionGGXFromNdotH(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    denom = PI * denom * denom;
    return a2 / max(denom, 0.0001);
}

float glassFresnel(float3 N, float3 V)
{
    float viewFacing = max(dot(N, V), 0.0);
    return pow(saturate(1.0 - viewFacing), 5.0);
}

// Environment BRDF (scale, bias): sampled from the CPU-baked DFG table (see dfgLut).
// specular = F0·scale + f90·bias; the F0=1 response for energy compensation is scale + bias.
float2 envBrdfAb(float roughness, float NdotV)
{
    return dfgLut.SampleLevel(skySpecularSampler, float2(NdotV, roughness), 0.0).rg;
}

// Filament/Godot multiscatter energy compensation: single-scatter GGX loses energy at high
// roughness; metals (large F0) recover it multiplicatively. env = dfg.y = the F0=1 split-sum
// response. Applied to BOTH the direct specular lobe and the sky-reflection specular, exactly
// where Godot applies it (scene_forward_clustered.glsl).
float3 energyCompensation(float3 F0, float env)
{
    return 1.0 + F0 * (1.0 / max(env, 1e-3) - 1.0);
}

float3 applyGlassResponse(float3 litColor, float3 baseColor, float fresnel)
{
    float3 surfaceTint = baseColor * (0.025 + fresnel * 0.08);
    float3 reflectedEdge = float3(0.72, 0.88, 0.82) * fresnel * 0.09;
    return litColor * (0.20 + fresnel * 0.30) + surfaceTint + reflectedEdge;
}

// --- Shader entry points ---

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    VertexOutput output;
    output.position = mul(draw.mvp, float4(input.position, 1.0));
    output.worldPos = mul(draw.model, float4(input.position, 1.0)).xyz;
    output.objectPos = input.position;
    output.worldNormal = mul((float3x3)draw.normalMatrix, input.normal);
    output.texcoord = input.texcoord;
    output.worldTangent = float4(mul((float3x3)draw.normalMatrix, input.tangent.xyz), input.tangent.w);
    return output;
}

// KHR_texture_transform for the baseColor slot: uv' = Translate(offset) · Rotate(r) · Scale(s).
float2 transformBaseColorUv(float2 uv)
{
    float2 scaled = uv * material.uvOffsetScale.zw;
    float r = material.uvRotation.x;
    float c = cos(r);
    float s = sin(r);
    float2 rotated = float2(c * scaled.x + s * scaled.y, -s * scaled.x + c * scaled.y);
    return rotated + material.uvOffsetScale.xy;
}

float3 sampleNormal(float2 texcoord, float3 worldNormal, float4 worldTangent)
{
    // Normal maps are two-channel (BC5, or the RGBA fallback's swizzled equivalent): only R/G
    // carry data. Reconstruct Z from the scaled XY: z = sqrt(1 - x*x - y*y).
    float2 nxy = normalTexture.Sample(baseColorSampler, texcoord).xy * 2.0 - 1.0;
    nxy *= material.normalScale;
    float nz = sqrt(saturate(1.0 - dot(nxy, nxy)));
    float3 normalSample = float3(nxy, nz);

    float3 N = normalize(worldNormal);
    float3 T = normalize(worldTangent.xyz);
    T = normalize(T - N * dot(N, T));
    float3 B = cross(N, T) * worldTangent.w;

    return normalize(T * normalSample.x + B * normalSample.y + N * normalSample.z);
}

float getDistanceAttenuation(float distanceToLight, float range, float decay)
{
    if (range <= 0.0)
    {
        return 1.0;
    }

    // Godot's get_omni_attenuation: window² · pow(distance, -decay) with a (d/range)^4 cutoff. The
    // 4th-power window keeps the light near full strength before cutting sharply at the range boundary.
    // decay is the exported LIGHT_PARAM_ATTENUATION (Godot's default 1.0 is inverse-LINEAR, not
    // inverse-square) — matching it is what makes the light pool the right size/brightness.
    float nd = distanceToLight / range;
    nd *= nd;              // (d/range)^2
    nd *= nd;              // (d/range)^4
    float window = saturate(1.0 - nd);
    float smoothRangeFade = window * window;
    float dist = max(distanceToLight, 0.0001);
    return smoothRangeFade * pow(dist, -decay);
}

float getSpotAttenuation(SceneLight light, float3 surfaceToLight)
{
    // Godot's spot cone (light_process_spot): a smooth radial falloff from the cone axis (full) to the
    // edge (zero) — NOT a hard-edged disc. spotRim is 0 on-axis, 1 at the cone boundary; Godot raises it
    // to spot_angle_attenuation (default 1.0, assumed here → linear rim). The previous inner/outer blend
    // drew a hard "circle" on the ground whenever no inner angle was authored (Godot has no inner angle),
    // because it fell back to inner == outer. spotAngles.y (inner angle) is unused in this model.
    float3 lightToSurface = -surfaceToLight;
    float3 coneDirection = -normalize(light.directionAndRange.xyz);
    float cosTheta = dot(lightToSurface, coneDirection);
    float coneCos = cos(radians(light.spotAngles.x * 0.5)); // cosine of the cone half-angle
    float scos = max(cosTheta, coneCos);
    float denom = max(1.0 - coneCos, 1e-4); // guards a degenerate 0°-outer-angle spot (coneCos == 1)
    float spotRim = max(1e-4, (1.0 - scos) / denom);
    return 1.0 - spotRim; // 1 - pow(spotRim, spot_angle_attenuation); default exponent 1
}

// Shadow bias. ShadowNormalBias nudges the receiver sample along its surface NORMAL before the shadow
// lookup — this lifts a grazing self-shadowing surface (the ground under an angled sun) off itself to
// kill the moiré weave WITHOUT the along-light offset a large depth bias adds, which pushes object
// shadows off their contact point (peter-panning). It scales with tan(angle to light) since acne
// worsens at grazing angles, capped at ShadowMaxNormalBias so the tan() blow-up near grazing incidence
// (N·L → 0) can't displace the sample by more than a fixed world-space amount — otherwise a spot/point
// light's tighter frustum could have the sample land outside its footprint. ShadowDepthBias is a small
// constant for the residual. World-space units, sized for the directional map (widest frustum / largest
// texels); a mild over-offset on the tighter spot/point maps is harmless. Normal-offset rather than
// depth-only is what lets contact shadows stay attached (matching Godot) while the ground stays acne-free.
static const float ShadowDepthBias = 0.0004;
static const float ShadowNormalBias = 0.05;
// Tap spacing (in shadow texels) for the 3x3 soft-shadow PCF. Wider than 1 texel to broaden the
// penumbra toward Godot's softer shadow edges.
static const float ShadowMaxNormalBias = 0.15;

// Cube face index (point lights) from the dominant axis of the light→surface direction.
int getPointShadowFace(float3 dir)
{
    float3 a = abs(dir);
    if (a.x >= a.y && a.x >= a.z) return dir.x > 0.0 ? 0 : 1;
    if (a.y >= a.z)               return dir.y > 0.0 ? 2 : 3;
    return dir.z > 0.0 ? 4 : 5;
}

// Light visibility in [0,1]: 1 = fully lit, (1 - strength) = fully shadowed. Samples the shadow-map
// array — one full-resolution layer per shadow view (dir/spot = 1, point = 6 cube faces) — via the
// light's per-face view-projection. No tile UV math and no seam clamp: each layer owns all of [0,1].
// Interleaved Gradient Noise (Godot's quick_hash) — decorrelates the per-pixel PCF disk rotation.
float shadowDiskHash(float2 pos)
{
    const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(pos, magic.xy)));
}

float sampleSceneLightShadow(SceneLight light, int lightIndex, float3 worldPos, float3 N, float3 surfaceToLight, float2 fragCoord)
{
    if (light.spotAngles.w <= 0.0 || light.shadowAtlas.y <= 0.0 || light.spotAngles.z < 0.0)
        return 1.0; // no shadow for this light

    int face = 0;
    if (light.positionAndType.w > 0.5 && light.positionAndType.w < 1.5) // point light
        face = getPointShadowFace(-surfaceToLight);
    if ((float)face >= light.shadowAtlas.y)
        return 1.0;

    // Normal-offset bias: lift the receiver sample off its own surface along the normal, scaled by
    // tan(angle to light) so grazing surfaces (where acne appears) get more. Perpendicular to the
    // surface, so unlike a large depth bias it does NOT shift the sample along the light direction and
    // therefore does not detach object shadows from their contact point.
    float ndotl = saturate(dot(N, surfaceToLight));
    float slope = sqrt(saturate(1.0 - ndotl * ndotl)) / max(ndotl, 0.05); // tan(angle to light)
    float3 samplePos = worldPos + N * min(ShadowNormalBias * (1.0 + slope), ShadowMaxNormalBias);

    int matrixIndex = lightIndex * 6 + face;
    float4 shadowPos = mul(frame.sceneLightShadowMatrices[matrixIndex], float4(samplePos, 1.0));
    if (shadowPos.w <= 0.0)
        return 1.0;
    float3 ndc = shadowPos.xyz / shadowPos.w;
    float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5; // NDC → UV, Y flipped (WebGPU clip-Z [0,1])
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0)
        return 1.0; // outside the light frustum → treat as lit

    float layer = light.spotAngles.z + (float)face; // array slice for this view
    float texel = frame.shadowSettings.x;
    float compare = ndc.z - ShadowDepthBias;

    float lit;
    if (light.shadowAtlas.w > 0.5)
    {
        // Soft shadows: Godot's SOFT_LOW PCF — a 4-sample Vogel disk (r = sqrt((i+0.5)/4),
        // theta = 2.4i), radius 2 texels (quality radius x shadow_blur 1), randomly rotated per
        // pixel with interleaved gradient noise exactly like sample_directional_pcf_shadow.
        // The rotation trades the fixed-kernel banding for fine noise, which reads as a smooth
        // penumbra at screen scale (and matches Godot's own edge texture).
        static const float2 kVogel4[4] = {
            float2(0.353553, 0.0),
            float2(-0.451566, 0.413635),
            float2(0.069177, -0.787537),
            float2(0.569071, 0.742405),
        };
        float s = texel * 2.0;
        float r = shadowDiskHash(fragCoord) * 2.0 * PI;
        float cr = cos(r);
        float sr = sin(r);
        lit = 0.0;
        [unroll] for (int k = 0; k < 4; k++)
        {
            // Godot's mat2(vec2(cr,-sr), vec2(sr,cr)) * offset (column-major GLSL).
            float2 o = float2(cr * kVogel4[k].x + sr * kVogel4[k].y,
                              -sr * kVogel4[k].x + cr * kVogel4[k].y);
            lit += shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv + o * s, layer), compare);
        }
        lit *= 0.25;
    }
    else
    {
        lit = shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv, layer), compare);
    }

    return lerp(1.0 - light.spotAngles.w, 1.0, lit);
}

float3 evaluateDirectLight(
    SceneLight light,
    int lightIndex,
    float3 worldPos,
    float3 N,
    float3 V,
    float3 F0,
    float3 baseColor,
    float metallic,
    float roughness,
    float3 energyComp,
    float2 fragCoord)
{
    float3 L = float3(0.0, 1.0, 0.0);
    float attenuation = 1.0;
    if (light.positionAndType.w < 0.5)
    {
        L = normalize(light.directionAndRange.xyz);
    }
    else
    {
        float3 lightVector = light.positionAndType.xyz - worldPos;
        float distanceToLight = length(lightVector);
        L = lightVector / max(distanceToLight, 0.0001);
        attenuation = getDistanceAttenuation(distanceToLight, light.directionAndRange.w, light.shadowAtlas.x);
        if (light.positionAndType.w > 1.5)
        {
            attenuation *= getSpotAttenuation(light, L);
        }
    }

    // Godot's size_A: an angular widening from the light's physical size — directional carries
    // 1−cos(angular) precomputed; point/spot derive it from radius/distance per fragment
    // (scene_forward_lights_inc.glsl: size = max(1 − 1/sqrt(1 + t²), 0), t = size/dist). It is
    // ADDED to the NdotL / LdotH / NdotH cosines like Godot does, softening highlights (and
    // slightly wrapping the diffuse terminator) for area-ish lights.
    float sizeA = 0.0;
    if (light.positionAndType.w < 0.5)
    {
        sizeA = light.sizeParams.x;
    }
    else if (light.sizeParams.x > 0.0)
    {
        float t = light.sizeParams.x / max(length(light.positionAndType.xyz - worldPos), 0.001);
        sizeA = max(1.0 - 1.0 / sqrt(1.0 + t * t), 0.0);
    }

    float NdotL = saturate(min(sizeA + dot(N, L), 1.0));
    float NdotV = max(dot(N, V), 0.0);
    float3 H = normalize(V + L);
    // Cook-Torrance (specular) + Lambert (diffuse), matching Godot's DEFAULT (non-physical light
    // units) light_compute: roughness feeds GGX directly (alpha = roughness²), specular carries the
    // 1/(4·NdotV·NdotL) term. Diffuse is albedo WITHOUT the 1/π — in Godot's non-physical mode
    // light_energy is a raw radiance multiplier that already absorbs the π, so dividing here makes
    // direct lighting ~π× too dim (measured against Godot). No arbitrary intensity scales — the
    // exported light_energy is the single source of brightness. Diffuse and specular are on Godot's
    // non-physical basis (NOT a physically-conserving energy split) — don't reintroduce /π on either
    // lobe without re-checking pixel-diff captures against Godot.
    // Specular: an exact port of Godot's light_compute isotropic GGX lobe
    // (scene_forward_lights_inc.glsl) so the two hosts agree per-pixel:
    //   D  — GGX with α = roughness² (identical to the standard Trowbridge-Reitz form).
    //   V  — Hammon's visibility approximation 0.5/mix(2·NL·NV, NL+NV, α), which FOLDS the
    //        Smith G and the 1/(4·NL·NV) denominator into one term (do not divide again).
    //   F  — Schlick with Filament's specular-occlusion f90 = clamp(dot(f0, 16.67), metallic, 1).
    // Godot premultiplies light energy by π (non-physical mode) and divides only the DIFFUSE
    // lobe back by π — its specular keeps the π, scaled by LIGHT_PARAM_SPECULAR (exported in
    // shadowAtlas.z; Godot default 0.5).
    float alphaGgx = roughness * roughness;
    float cNdotH = saturate(sizeA + dot(N, H));
    float D = distributionGGXFromNdotH(cNdotH, roughness);
    float Vis = saturate(0.5 / lerp(2.0 * NdotL * NdotV, NdotL + NdotV, alphaGgx));
    float LdotH = saturate(sizeA + dot(L, H));
    float f90 = clamp(dot(F0, float3(16.67, 16.67, 16.67)), metallic, 1.0);
    float m = 1.0 - LdotH;
    float m2 = m * m;
    float3 F = F0 + (f90 - F0) * (m2 * m2 * m);
    float specularAmount = light.shadowAtlas.z;
    // energyComp: Filament/Godot multiscatter compensation (computed once per fragment).
    float3 specular = D * Vis * F * (PI * specularAmount) * energyComp;
    // Godot's default Lambert has NO (1-F) energy split against the specular lobe — only the
    // metallic suppression. Matching that (dropping the (1-F)) restores grazing-angle diffuse.
    float3 kD = float3(1.0 - metallic, 1.0 - metallic, 1.0 - metallic);
    float3 diffuse = kD * baseColor;
    float3 radiance = light.colorAndIntensity.rgb * light.colorAndIntensity.w * attenuation;
    float shadow = sampleSceneLightShadow(light, lightIndex, worldPos, N, L, fragCoord);
    return (diffuse + specular) * radiance * NdotL * shadow;
}

// Widen roughness based on how fast the shading normal changes across the screen.
// Where the normal varies quickly (high-frequency detail), the GGX highlight would
// alias into sparkle; raising roughness there keeps it stable.
float applySpecularAntiAliasing(float roughness, float3 N)
{
    float3 dNdx = ddx(N);
    float3 dNdy = ddy(N);
    float variance = frame.aaSettings.y * (dot(dNdx, dNdx) + dot(dNdy, dNdy));
    float kernelRoughness2 = min(2.0 * variance, frame.aaSettings.z);
    float alpha2 = saturate(roughness * roughness + kernelRoughness2);
    return sqrt(alpha2);
}

// Tone operators ported from Godot's tone_mapping_inc.glsl so an exported scene renders with the
// same tonemap it was authored under (Environment.tonemap_mode). Each is normalized by the white
// point where Godot's is. The active operator + exposure + white come from frame.shadowSettings.yzw
// (exported from the Godot Environment — see PbrTonemap).

// Reinhard (extended, white-point normalized) — Godot mode 1.
float3 tonemapReinhard(float3 color, float white)
{
    return (color * (1.0 + color / (white * white))) / (1.0 + color);
}

// Filmic (modified Hable/Uncharted-2, fixed exposure_bias = 2) — Godot mode 2.
float3 tonemapFilmic(float3 color, float white)
{
    const float expBias = 2.0;
    const float A = 0.22 * expBias * expBias;
    const float B = 0.30 * expBias;
    const float C = 0.10;
    const float D = 0.20;
    const float E = 0.01;
    const float F = 0.30;
    float3 c = ((color * (A * color + C * B) + D * E) / (color * (A * color + B) + D * F)) - E / F;
    float w = ((white * (A * white + C * B) + D * E) / (white * (A * white + B) + D * F)) - E / F;
    return c / w;
}

// ACES filmic (Narkowicz fit) — a close approximation of Godot mode 3.
float3 tonemapAces(float3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

// AgX (mode 4) — the allenwp-curve AgX Godot has used since 4.4 (matches this project's Godot 4.7).
// Ported from godot/servers/rendering/renderer_rd/shaders/effects/tonemap.glsl (tonemap_agx +
// allenwp_curve). The curve params are precomputed for DEFAULT AgX (contrast 1.25, white 2.0, SDR
// output_max 1.0) — exactly the values Godot's environment_storage.cpp computes on the CPU for a
// default AgX environment. Returns LINEAR (no OETF here; the fragment entry applies it).
float3 agxAllenwpCurve(float3 x)
{
    const float awpCrossover = 0.18;      // 18% middle gray
    const float awpShoulderMax = 0.82;    // output_max_value(1.0) - crossover
    const float awpContrast = 1.25;
    const float awpToeA = 0.534111561;    // CPU-precomputed (see environment_storage.cpp)
    const float awpSlope = 1.025000000;
    const float awpW = 4.140500000;

    // Reinhard-like shoulder (x >= crossover).
    float3 s = x - awpCrossover;
    float3 slopeS = awpSlope * s;
    s = slopeS * (1.0 + s / awpW) / (1.0 + slopeS / awpShoulderMax) + awpCrossover;
    // Sigmoid-power toe (x < crossover).
    float3 t = pow(max(x, 0.0), awpContrast);
    t = t / (t + awpToeA);
    return float3(
        x.x < awpCrossover ? t.x : s.x,
        x.y < awpCrossover ? t.y : s.y,
        x.z < awpCrossover ? t.z : s.z);
}

float3 tonemapAgx(float3 color)
{
    // Combined Rec.709→Rec.2020 + AgX inset (row-major for mul(M, v)).
    const float3x3 agxInset = float3x3(
        0.544814746488245, 0.373787398372697,  0.0813978551390581,
        0.140416948464053, 0.754137554567394,  0.105445496968552,
        0.0888104196149096, 0.178871756420858, 0.732317823964232);
    // Combined inverse AgX outset + Rec.2020→Rec.709.
    const float3x3 agxOutset = float3x3(
        1.96488741169489,   -0.855988495690215, -0.108898916004672,
        -0.299313364904742,  1.32639796461980,  -0.0270845997150571,
        -0.164352742528393, -0.238183969428088,  1.40253671195648);

    color = mul(agxInset, max(color, 0.0));
    color = agxAllenwpCurve(color);
    color = min(1.0, color);              // clip to output_max_value (SDR) — kills bright-input cyan
    color = mul(agxOutset, color);
    return color;                         // linear; may carry small negatives (as in Godot)
}

// Apply the exported tone operator. mode: 0 Linear, 1 Reinhard, 2 Filmic (pixel-verified vs Godot),
// 3 ACES (Narkowicz approx), 4 AgX (Godot 4.4+ allenwp AgX, default params).
float3 applyTonemap(float3 color, float exposure, float white, float mode)
{
    color = max(color * exposure, 0.0);
    white = max(white, 1e-4);
    if (mode < 0.5) return color;                        // Linear
    if (mode < 1.5) return tonemapReinhard(color, white);
    if (mode < 2.5) return tonemapFilmic(color, white);
    if (mode < 3.5) return tonemapAces(color);           // ACES (approx)
    return tonemapAgx(color);                            // AgX
}

// Screen-space ambient occlusion from the world-position pre-pass. A spiral of screen-space taps
// around the fragment; each reconstructs the neighbour's world position (sampled directly, no matrix
// math) and occludes when it's within `radius` of the fragment and above its tangent plane. Returns
// a [0,1] AO multiplier applied to ambient only. Reading positionTexture via textureLoad avoids the
// depth-format sampling hazard.
float computeSsao(float2 fragCoord, float3 worldPos, float3 N)
{
    float intensity = ssao.params.x;
    if (intensity <= 0.0) return 1.0; // SSAO disabled

    float radius = ssao.params.y;
    float bias = ssao.params.z;
    float power = max(ssao.params.w, 1e-3);
    float2 screenSize = ssao.screen.zw;

    float rot = frac(sin(dot(fragCoord, float2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
    float cosr = cos(rot), sinr = sin(rot);
    float pixelRadius = 26.0 * radius; // screen search extent; world-distance below enforces correctness

    const int SAMPLES = 12;
    float occlusion = 0.0;
    for (int i = 0; i < SAMPLES; i++)
    {
        float a = (float(i) + 0.5) / float(SAMPLES);
        float ang = a * 6.2831853 * 3.0;
        float2 dir = float2(cos(ang), sin(ang));
        float2 rdir = float2(dir.x * cosr - dir.y * sinr, dir.x * sinr + dir.y * cosr);
        int2 samplePix = int2(fragCoord + rdir * a * pixelRadius);
        if (samplePix.x < 0 || samplePix.y < 0 || samplePix.x >= (int)screenSize.x || samplePix.y >= (int)screenSize.y) continue;

        float4 p = positionTexture.Load(int3(samplePix, 0));
        if (p.w < 0.5) continue; // background (cleared to 0)

        float3 diff = p.xyz - worldPos;
        float dist = length(diff);
        if (dist < 1e-3 || dist > radius) continue;

        float ndl = dot(N, diff / dist);
        if (ndl <= bias) continue;
        occlusion += ndl * (1.0 - dist / radius);
    }
    occlusion /= float(SAMPLES);
    return pow(saturate(1.0 - occlusion * intensity), power);
}

// ---- procedural material library (noise + per-kind recipes) ----
// Mirrors data/ui-adjacent Godot GDShader (kept in lockstep by a recipe-parity test). Object-space
// p (scaled by procParams.y), time t (elapsed·flowSpeed). Returns albedo/emissive/roughness/metallic
// that override the sampled PBR surface, then flow through the normal lighting (so a lava ball still
// reflects the sky). Emissive is HDR (× emissiveStrength) so bright recipes bloom.

float hash31(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float3 hash33(float3 p)
{
    p = float3(dot(p, float3(127.1, 311.7, 74.7)),
               dot(p, float3(269.5, 183.3, 246.1)),
               dot(p, float3(113.5, 271.9, 124.6)));
    return frac(sin(p) * 43758.5453);
}

float valueNoise(float3 x)
{
    float3 i = floor(x);
    float3 f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(lerp(hash31(i + float3(0,0,0)), hash31(i + float3(1,0,0)), f.x),
                     lerp(hash31(i + float3(0,1,0)), hash31(i + float3(1,1,0)), f.x), f.y),
                lerp(lerp(hash31(i + float3(0,0,1)), hash31(i + float3(1,0,1)), f.x),
                     lerp(hash31(i + float3(0,1,1)), hash31(i + float3(1,1,1)), f.x), f.y), f.z);
}

float fbm(float3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++) { v += a * valueNoise(p); p *= 2.02; a *= 0.5; }
    return v;
}

// Worley (cellular) F1 distance — facets, frost cells, flecks.
float worley(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float d = 1.0;
    for (int x = -1; x <= 1; x++)
    for (int y = -1; y <= 1; y++)
    for (int z = -1; z <= 1; z++)
    {
        float3 g = float3(x, y, z);
        float3 o = hash33(i + g);
        float3 r = g + o - f;
        d = min(d, dot(r, r));
    }
    return sqrt(d);
}

struct ProcResult { float3 albedo; float3 emissive; float roughness; float metallic; };

ProcResult evalProcedural(int kind, float3 p, float t, float3 colorA, float3 colorB)
{
    ProcResult r;
    r.albedo = colorA;
    r.emissive = float3(0.0, 0.0, 0.0);
    r.roughness = 0.4;
    r.metallic = 0.0;

    if (kind == 1) // lava — dark crust with glowing, flowing molten cracks
    {
        float3 q = p + float3(0.0, t, 0.0) * 0.35;
        float n = fbm(q * 1.5);
        float cracks = fbm(q * 3.0 + n);
        float hot = smoothstep(0.42, 0.72, cracks);
        r.albedo = lerp(float3(0.05, 0.02, 0.01), float3(0.14, 0.06, 0.03), n);
        float3 glow = lerp(float3(0.95, 0.16, 0.02), float3(1.0, 0.75, 0.12), smoothstep(0.5, 0.95, cracks));
        r.emissive = glow * hot;
        r.roughness = 0.65;
    }
    else if (kind == 2) // marble — veined stone
    {
        float n = fbm(p * 2.0);
        float veins = abs(sin((p.x + p.y * 0.6 + n * 3.5) * 3.2));
        veins = pow(1.0 - veins, 2.2);
        r.albedo = lerp(colorA, colorB, veins);
        r.roughness = 0.28;
    }
    else if (kind == 3) // jade — layered translucent green with inner depth
    {
        float n = fbm(p * 2.5 + t * 0.12);
        float depth = fbm(p * 5.0 + n * 2.2);
        r.albedo = lerp(float3(0.05, 0.32, 0.18), float3(0.22, 0.72, 0.46), depth);
        r.emissive = float3(0.02, 0.11, 0.07) * smoothstep(0.4, 0.85, depth);
        r.roughness = 0.2;
    }
    else if (kind == 4) // ice — frosted worley cells + fine frost
    {
        float cells = worley(p * 3.0);
        float frost = fbm(p * 6.0 + t * 0.06);
        r.albedo = lerp(float3(0.58, 0.78, 0.95), float3(0.88, 0.94, 1.0), cells) + frost * 0.08;
        r.emissive = float3(0.05, 0.08, 0.13) * (1.0 - cells);
        r.roughness = 0.07 + cells * 0.1;
    }
    else if (kind == 5) // molten metal — flowing brushed metallic
    {
        float n = fbm(p * 3.0 + t * 0.25);
        r.albedo = colorA * (0.78 + n * 0.44);
        r.roughness = 0.14 + n * 0.16;
        r.metallic = 1.0;
    }
    else if (kind == 6) // obsidian — near-black volcanic glass, subtle swirl
    {
        float n = fbm(p * 4.0);
        r.albedo = lerp(float3(0.01, 0.01, 0.02), float3(0.06, 0.04, 0.09), n);
        r.roughness = 0.05 + n * 0.05;
    }
    else if (kind == 7) // gem — faceted translucent, tinted, faint internal glow
    {
        float facets = worley(p * 4.0);
        r.albedo = lerp(colorA * 0.45, colorA, facets);
        r.emissive = colorA * 0.18 * facets;
        r.roughness = 0.1;
    }
    else if (kind == 8) // amber — warm translucent resin with glowing flecks
    {
        float n = fbm(p * 3.0);
        float flecks = smoothstep(0.68, 0.74, worley(p * 8.0));
        r.albedo = lerp(float3(0.55, 0.28, 0.05), float3(0.9, 0.55, 0.16), n);
        r.emissive = float3(0.3, 0.12, 0.02) * n + float3(1.0, 0.6, 0.2) * flecks * 0.6;
        r.roughness = 0.16;
    }
    else if (kind == 9) // nebula/quartz — swirling two-tone with soft glow
    {
        float ang = fbm(p * 2.0 + t * 0.15);
        float swirl = fbm(p * 4.0 + ang * 3.0);
        r.albedo = lerp(colorA, colorB, swirl);
        r.emissive = colorB * 0.12 * smoothstep(0.5, 0.92, swirl);
        r.roughness = 0.15;
    }
    return r;
}

float4 shadePbr(FragmentInput input)
{
    float2 baseUv = transformBaseColorUv(input.texcoord);
    float4 baseColorSample = baseColorTexture.Sample(baseColorSampler, baseUv);
    float4 baseColor = baseColorSample * material.baseColorFactor;

    float4 metallicRoughnessSample = metallicRoughnessTexture.Sample(baseColorSampler, input.texcoord);
    float metallic = saturate(material.metallicFactor * metallicRoughnessSample.b);
    float roughness = max(material.roughnessFactor * metallicRoughnessSample.g, 0.04); // clamp to avoid division by zero

    // Procedural material: override the sampled surface from a noise recipe (object-space, animated
    // by frame.time). The result then flows through the normal PBR lighting below.
    float3 procEmissive = float3(0.0, 0.0, 0.0);
    int procKind = int(material.procParams.x + 0.5);
    if (procKind >= 1)
    {
        ProcResult pr = evalProcedural(
            procKind, input.objectPos * material.procParams.y, frame.time.x * material.procParams.z,
            material.procColorA.rgb, material.procColorB.rgb);
        baseColor = float4(pr.albedo, baseColor.a);
        roughness = max(pr.roughness, 0.04);
        metallic = pr.metallic;
        procEmissive = pr.emissive * material.procParams.w;
    }

    // Back-facing fragments shade with the negated geometric normal (Godot's double-sided
    // convention: `normal_interp * (FRONT_FACING ? 1.0 : -1.0)` before normal mapping).
    float3 geometricNormal = input.isFrontFace ? input.worldNormal : -input.worldNormal;
    float3 N = sampleNormal(input.texcoord, geometricNormal, input.worldTangent);
    roughness = applySpecularAntiAliasing(roughness, N);
    float3 V = normalize(frame.cameraPos.xyz - input.worldPos);

    // Dielectric F0 = 0.04, metallic F0 = albedo
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), baseColor.rgb, metallic);

    // Filament/Godot multiscatter energy compensation, computed once per fragment from the
    // split-sum env BRDF (Godot: get_energy_compensation(f0, prefiltered_dfg(...).y)); it
    // multiplies every specular lobe — each direct light and the sky reflection.
    float2 envAb = envBrdfAb(roughness, clamp(dot(N, V), 0.0001, 1.0));
    float3 energyComp = energyCompensation(F0, envAb.x + envAb.y);

    // Direct lighting. Forward+: point/spot lights are skipped unless their bit is set in
    // this fragment's froxel mask (directional lights always shade). The mask defaults to
    // all-ones when clustering is disabled (clusterParams.x < 1).
    int sceneLightCount = min((int)frame.ambientEquator.w, MaxSceneLights);
    uint clusterMask0 = 0xffffffffu;
    uint clusterMask1 = 0xffffffffu;
    if (frame.clusterParams.x >= 1.0)
    {
        int tilesX = (int)frame.clusterParams.x;
        int tilesY = (int)frame.clusterParams.y;
        int zSlices = (int)frame.clusterParams.z;
        int tileX = clamp((int)(input.position.x / 32.0), 0, tilesX - 1);
        int tileY = clamp((int)(input.position.y / 32.0), 0, tilesY - 1);
        float nearZ = frame.cameraForward.w;
        float farZ = frame.clusterParams.w;
        float viewZ = max(dot(input.worldPos - frame.cameraPos.xyz, frame.cameraForward.xyz), nearZ);
        int slice = clamp((int)(log(viewZ / nearZ) / log(farZ / nearZ) * (float)zSlices), 0, zSlices - 1);
        int froxel = ((slice * tilesY + tileY) * tilesX + tileX) * 2;
        clusterMask0 = clusterMasks[froxel];
        clusterMask1 = clusterMasks[froxel + 1];
    }
    float3 Lo = float3(0.0, 0.0, 0.0);
    for (int i = 0; i < sceneLightCount; i++)
    {
        uint maskWord = ((uint)i >> 5) == 0u ? clusterMask0 : clusterMask1;
        if (frame.sceneLights[i].positionAndType.w >= 0.5 && (maskWord & (1u << ((uint)i & 31u))) == 0u)
            continue;
        Lo += evaluateDirectLight(
            frame.sceneLights[i],
            i,
            input.worldPos,
            N,
            V,
            F0,
            baseColor.rgb,
            metallic,
            roughness,
            energyComp,
            input.position.xy);
    }

    // Ambient: L2 SH sky irradiance per-normal (Godot's sky-SH ambient) when provided; the
    // 3-zone hemisphere blend remains as the fallback, and the flat mode overrides both.
    float3 hemisphereColor;
    if (frame.ambientGround.w > 0.5)
    {
        hemisphereColor = frame.ambient.rgb; // flat ambient
    }
    else if (frame.ambientSh[0].w > 0.5)
    {
        // E/π(n) = Σ ĉ_i · Y_i(n): band factors are premultiplied into the coefficients at
        // export; the basis constants (Ramamoorthi & Hanrahan) are applied here.
        float3 n = N;
        float3 sh = frame.ambientSh[0].rgb * 0.282095;
        sh += frame.ambientSh[1].rgb * (0.488603 * n.y);
        sh += frame.ambientSh[2].rgb * (0.488603 * n.z);
        sh += frame.ambientSh[3].rgb * (0.488603 * n.x);
        sh += frame.ambientSh[4].rgb * (1.092548 * n.x * n.y);
        sh += frame.ambientSh[5].rgb * (1.092548 * n.y * n.z);
        sh += frame.ambientSh[6].rgb * (0.315392 * (3.0 * n.z * n.z - 1.0));
        sh += frame.ambientSh[7].rgb * (1.092548 * n.x * n.z);
        sh += frame.ambientSh[8].rgb * (0.546274 * (n.x * n.x - n.y * n.y));
        hemisphereColor = max(sh, 0.0);
    }
    else
    {
        float3 skyColor = frame.ambient.rgb;
        float3 equatorColor = frame.ambientEquator.rgb;
        float3 groundColor = frame.ambientGround.rgb;
        float hemisphere = dot(N, float3(0.0, 1.0, 0.0)) * 0.5 + 0.5;
        hemisphereColor = hemisphere < 0.5
            ? lerp(groundColor, equatorColor, hemisphere * 2.0)
            : lerp(equatorColor, skyColor, (hemisphere - 0.5) * 2.0);
    }
    float occlusionSample = occlusionTexture.Sample(baseColorSampler, input.texcoord).r;
    float occlusion = lerp(1.0, occlusionSample, material.occlusionStrength);
    // Screen-space AO (from the world-position pre-pass) also darkens only the ambient, like Godot.
    occlusion *= computeSsao(input.position.xy, input.worldPos, N);
    float exposure = max(frame.ambient.a, 0.0);
    float3 ambient = hemisphereColor * exposure * baseColor.rgb * (1.0 - metallic) * occlusion;

    // Sky-reflection specular (Godot reflected_light_source = Sky): GGX-prefiltered sky radiance
    // from the CPU LUT × the split-sum environment BRDF. The LUT has two row halves: the gradient
    // (u = reflection.y, rows 0..7) and the sun disk/halo (u = cos angle to the sun — radially
    // symmetric around the sun direction, so this is exact; rows 8..15, stored ÷4 for 8-bit HDR
    // headroom). The sun direction/colour come from the first directional scene light — the same
    // light the sky background pairs with, so a disabled sun vanishes from reflections too. AO
    // darkens it all like the diffuse ambient (Godot applies AO to all ambient light).
    if (frame.aaSettings.x > 0.5)
    {
        float3 R = reflect(-V, N);
        float NdotV = clamp(dot(N, V), 0.0001, 1.0);
        float vHalf = (clamp(roughness, 0.0, 1.0) * 7.0 + 0.5) / 16.0;
        float3 prefiltered = skySpecularLut.Sample(skySpecularSampler, float2(R.y * 0.5 + 0.5, vHalf)).rgb;
        for (int li = 0; li < sceneLightCount; li++)
        {
            if (frame.sceneLights[li].positionAndType.w < 0.5)
            {
                float cosToSun = dot(R, normalize(frame.sceneLights[li].directionAndRange.xyz));
                prefiltered += skySpecularLut.Sample(skySpecularSampler,
                    float2(cosToSun * 0.5 + 0.5, 0.5 + vHalf)).rgb * 4.0;
                break;
            }
        }
        // Godot's indirect-specular combine (scene_forward_clustered.glsl): the split-sum pair
        // with the cheap-luminance f90 and the multiscatter energy compensation (envAb/energyComp
        // computed once per fragment above).
        float f90i = clamp(50.0 * F0.g, metallic, 1.0);
        ambient += prefiltered * energyComp * (F0 * envAb.x + f90i * envAb.y) * occlusion;
    }

    float3 color = ambient + Lo;
    float transmission = saturate(material.emissiveFactor.w);
    if (transmission > 0.0)
    {
        float fresnel = glassFresnel(N, V);
        color = lerp(color, applyGlassResponse(color, baseColor.rgb, fresnel), transmission);
        baseColor.a = saturate(baseColor.a * lerp(1.0, 0.12, transmission) + fresnel * 0.05);
    }

    // Emissive
    color += emissiveTexture.Sample(baseColorSampler, input.texcoord).rgb * material.emissiveFactor.rgb;
    color += procEmissive; // HDR procedural emissive (lava cracks, gem glow) — blooms in composite

    // Highlight flash (selection/hit feedback): additive push toward white.
    color += draw.highlight.x * 0.35;

    // Return LINEAR HDR — tonemapping moved to the composite pass (composite.slang), which
    // tonemaps the whole HDR scene target once (enabling bloom + a single consistent curve).
    // The tone operators above are retained for reference/parity but are no longer called here.
    return float4(color, baseColor.a);
}

float3 linearToSrgb(float3 linearColor)
{
    // IEC 61966-2-1 OETF, per channel.
    float3 lo = linearColor * 12.92;
    float3 hi = 1.055 * pow(max(linearColor, 0.0), 1.0 / 2.4) - 0.055;
    return float3(
        linearColor.x <= 0.0031308 ? lo.x : hi.x,
        linearColor.y <= 0.0031308 ? lo.y : hi.y,
        linearColor.z <= 0.0031308 ? lo.z : hi.z);
}

// For *UnormSrgb swapchain formats: return linear, the hardware encodes on store.
[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    return shadePbr(input);
}

// For non-sRGB-only surfaces (e.g. D3D12 swapchains): encode in-shader. Exactly one of the
// two entry points is picked per pipeline based on the surface format — never both, so the
// image is never double-encoded.
[shader("fragment")]
float4 fragmentMainSrgb(FragmentInput input) : SV_Target
{
    float4 color = shadePbr(input);
    return float4(linearToSrgb(color.rgb), color.a);
}
