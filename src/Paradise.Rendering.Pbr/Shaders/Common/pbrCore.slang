// pbrCore.slang — the reusable body of the PBR shader: every helper, the vertex transform, and
// the full lighting path, with two extension seams so a GAME shader can reuse all of it without
// editing engine files:
//
//   pbrTransformVertex(...)  — the vertex transform, taking an object-space position a custom
//                              vertex entry may displace first (water waves, wind sway).
//   shadePbrCore(...)        — the whole lighting path (direct + shadows + ambient/SH + sky
//                              specular + SSAO + specular AA + glass + tonemap contract) given a
//                              RESOLVED surface: the caller owns N and the material sample.
//   shadePbr(...)            — the stock texture-sampling wrapper over shadePbrCore; the built-in
//                              entry points use it unchanged.
//
// A game shader does `#include "Common/pbrCore.slang"` (resolved via the -I dir the NuGet's build
// targets add), declares its own group-2 bindings from slot 7 up, and defines its own entry
// points. See Paradise.Rendering.Pbr.Test/Shaders/waterFixture.slang for the canonical example.
#ifndef PARADISE_PBR_CORE_SLANG
#define PARADISE_PBR_CORE_SLANG

#include "uniforms.slang"

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
    // Degenerate tangent guard. Meshes without UVs get the loader's default +X tangent
    // ("attribute gaps are default-filled"), and any face whose normal PARALLELS the
    // transformed tangent makes the Gram-Schmidt below normalize a zero vector. That NaN
    // is invisible in the lighting math (saturate() flushes it) but reaches the shadow
    // lookup through the normal-offset bias, where a NaN uv slips past the range guards
    // (NaN compares false) into SampleCmp — undefined, and in practice a per-frame coin
    // flip that strobed every ±X-facing panel of a rotating caster. No tangent basis
    // means no normal mapping: shade with the geometric normal instead.
    float3 tRaw = worldTangent.xyz - N * dot(N, worldTangent.xyz);
    if (dot(tRaw, tRaw) < 1e-8)
        return N;
    float3 T = normalize(tRaw);
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

// Shadow bias — two mechanisms, each sized by the map's ACTUAL texel world size (per light in
// sizeParams.y: directional = metres, point/spot = metres per metre of receiver distance, since
// perspective texels grow with range). A fixed world-space bias is only ever correct for one texel
// size — the old 0.05 m constant was tuned when the camera-fitted 4096 sun map made ~2.5 cm texels,
// and any coarser map (a scene authoring a smaller ShadowMapSize, a wider legacy fit) outgrew it and
// flat ground self-shadowed in diagonal bands.
//
// 1. NORMAL-OFFSET bias: lift the receiver sample along its surface normal, scaled up toward
//    grazing incidence (capped — the tan() blow-up at N·L → 0 must not push the sample outside
//    the light's footprint). With the plane bias below covering the PCF disk, this only has to
//    cover the CENTRE texel's own depth quantization — half a texel times the slope — and it must
//    stay that small: every texel of lift is a strip of CONTACT LIGHT-LEAK beside each caster
//    (near a wall's base the occluder's stored depth is nearly the receiver's own, so any lift
//    reads the base as non-occluding and the shadow fades toward the contact line — visibly, on
//    a coarse map, where a texel is tens of centimetres). Perpendicular to the surface, so unlike
//    a large depth bias it does not shift the sample along the light and does not detach object
//    shadows from their contact point.
// 2. RECEIVER-PLANE depth bias (Isidoro): each PCF tap compares against the receiver PLANE's depth
//    at the tap's own location — the plane's depth gradient in light NDC is solved from two
//    projected surface tangents — instead of the centre depth. Without it the disk's outer taps
//    need the normal offset to cover the WHOLE disk radius (blur × texel × slope), which at coarse
//    resolutions is metres of lift and visibly eats every short shadow; with it the offset stays
//    at ~1 texel no matter the blur.
//
// ShadowDepthBias is a small constant NDC residual on top. Together these keep flat ground
// acne-free at every map resolution while contact shadows stay attached (matching Godot).
static const float ShadowDepthBias = 0.0004;
// Lift in texels = ShadowNormalBiasTexels · (1 + tan(angle to light)), capped: the half-texel
// centre quantization needs ~0.5·tan texels of lift (the geometry works out to 0.5·sinθ/cos²θ),
// and every texel beyond that widens the contact light-leak strip. The cap bounds the grazing
// blow-up; past it the surface is nearly edge-on to the light and receives little direct light
// anyway, so residual acne there stays invisible.
static const float ShadowNormalBiasTexels = 0.5;
static const float ShadowMaxSlopeTexels = 2.0;    // cap on the total lift, in texels
static const float ShadowPlaneBiasClamp = 0.01;   // NDC cap on the per-tap plane adjustment

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

    // Normal-offset bias (mechanism 1 above): the centre texel's quantization, more toward
    // grazing, capped — and no more, or the contact strip beside every caster lightens.
    float texelWorld = light.sizeParams.y;
    if (light.positionAndType.w > 0.5) // perspective (point/spot): texels grow with distance
        texelWorld *= length(light.positionAndType.xyz - worldPos);
    float ndotl = saturate(dot(N, surfaceToLight));
    float slope = sqrt(saturate(1.0 - ndotl * ndotl)) / max(ndotl, 0.05); // tan(angle to light)
    float liftTexels = min(ShadowNormalBiasTexels * (1.0 + slope), ShadowMaxSlopeTexels);
    float3 samplePos = worldPos + N * (texelWorld * liftTexels);

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
        // Soft shadows: Godot's SOFT_HIGH-class PCF — a 16-sample Vogel disk
        // (r = sqrt((i+0.5)/16), theta = 2.4i), disk radius frame.shadowFilter.x shadow texels
        // (scene-authored; see Lighting.ShadowBlur in the export contract), randomly rotated
        // per pixel with interleaved gradient noise like sample_directional_pcf_shadow. The
        // rotation trades fixed-kernel banding for fine noise — but the noise AMPLITUDE goes as
        // 1/sqrt(taps), and it stops averaging out once the penumbra is WIDE on screen: on a
        // coarse map (a texel is tens of centimetres) 8 taps rendered every shadow edge as
        // visible diagonal stipple, IGN's own dither pattern. 16 taps is the tier that reads
        // as smooth there while the per-tap hardware 2x2 comparison (the sampler is Linear)
        // keeps the narrow-penumbra case dense. 4 taps at radius 2 (the old SOFT_LOW tier)
        // visibly sawtoothed even at fine texel sizes.
        static const float2 kVogel16[16] = {
            float2(+0.176777, +0.000000),
            float2(-0.225780, +0.206818),
            float2(+0.034587, -0.393769),
            float2(+0.284530, +0.371204),
            float2(-0.522210, -0.092451),
            float2(+0.494753, -0.314594),
            float2(-0.165602, +0.615488),
            float2(-0.315405, -0.607676),
            float2(+0.684569, +0.250232),
            float2(-0.712353, +0.293773),
            float2(+0.343624, -0.733602),
            float2(+0.253403, +0.809035),
            float2(-0.764550, -0.443523),
            float2(+0.897228, -0.196804),
            float2(-0.547908, +0.778490),
            float2(-0.125948, -0.976159),
        };
        float s = texel * frame.shadowFilter.x;
        float r = shadowDiskHash(fragCoord) * 2.0 * PI;
        float cr = cos(r);
        float sr = sin(r);

        // Receiver-plane depth bias (mechanism 2 in the bias comment): the plane's NDC depth
        // gradient, solved from two surface tangents projected through this face's matrix. Each
        // tap then compares against the PLANE's depth at its own offset rather than the centre's,
        // which is what lets the normal offset above stay at ~1 texel instead of covering the
        // whole disk. Exact for the ortho sun; first-order (and ample) for spot/point perspective.
        float3 tan1 = normalize(cross(N, abs(N.y) < 0.9 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));
        float3 tan2 = cross(N, tan1);
        float planeStep = max(texelWorld, 1e-4);
        float4 q1 = mul(frame.sceneLightShadowMatrices[matrixIndex], float4(samplePos + tan1 * planeStep, 1.0));
        float4 q2 = mul(frame.sceneLightShadowMatrices[matrixIndex], float4(samplePos + tan2 * planeStep, 1.0));
        float3 e1 = q1.xyz / max(q1.w, 1e-6) - ndc;
        float3 e2 = q2.xyz / max(q2.w, 1e-6) - ndc;
        float det = e1.x * e2.y - e1.y * e2.x;
        // dz per NDC-xy offset d: dz = dot(zGrad, d) with the 2x2 solve below; degenerate when the
        // plane is edge-on to the light (both tangents project to one line) — no adjustment then.
        float2 zGrad = abs(det) > 1e-9
            ? float2(e1.z * e2.y - e1.y * e2.z, e1.x * e2.z - e1.z * e2.x) / det
            : float2(0.0, 0.0);

        lit = 0.0;
        [unroll] for (int k = 0; k < 16; k++)
        {
            // Godot's mat2(vec2(cr,-sr), vec2(sr,cr)) * offset (column-major GLSL).
            float2 o = float2(cr * kVogel16[k].x + sr * kVogel16[k].y,
                              -sr * kVogel16[k].x + cr * kVogel16[k].y);
            float2 uvOff = o * s;
            // UV → NDC offset is (2, -2) (the Y flip from the uv mapping above).
            float dz = clamp(zGrad.x * (2.0 * uvOff.x) + zGrad.y * (-2.0 * uvOff.y),
                             -ShadowPlaneBiasClamp, ShadowPlaneBiasClamp);
            lit += shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv + uvOff, layer), compare + dz);
        }
        lit *= 0.0625;
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

// --- extension seams + the stock surface path ---

/// The rigid vertex transform from explicit object-space components. Extensions displace the
/// position (and adjust the normal/tangent if they can) before calling; the skinned entry point
/// feeds its blended components through the same path.
VertexOutput pbrTransformVertex(float3 localPosition, float3 localNormal, float2 texcoord, float4 localTangent)
{
    VertexOutput output;
    output.position = mul(draw.mvp, float4(localPosition, 1.0));
    output.worldPos = mul(draw.model, float4(localPosition, 1.0)).xyz;
    output.objectPos = localPosition;
    output.worldNormal = mul((float3x3)draw.normalMatrix, localNormal);
    output.texcoord = texcoord;
    output.worldTangent = float4(mul((float3x3)draw.normalMatrix, localTangent.xyz), localTangent.w);
    return output;
}

/// The displacement seam: the standard vertex stream plus an object-space offset. float3(0,0,0)
/// is the stock rigid path.
VertexOutput pbrTransformVertex(VertexInput input, float3 localOffset)
{
    return pbrTransformVertex(input.position + localOffset, input.normal, input.texcoord, input.tangent);
}

/// The fragment seam: the WHOLE lighting path given a resolved surface. The caller owns the
/// shading normal (already normal-mapped, gradient-derived, whatever) and the material sample:
///   N         — world-space shading normal (unit)
///   baseColor — rgba albedo (a = coverage for blend materials)
///   metallic / roughness — the metallic-roughness pair (roughness re-clamped here)
///   occlusion — baked/texture occlusion, 1 for none (SSAO is applied on top, inside)
///   emissive  — HDR emissive added after lighting (blooms in composite)
/// Returns LINEAR HDR + alpha, same contract as the built-in entry points.
float4 shadePbrCore(FragmentInput input, float3 N, float4 baseColor,
                    float metallic, float roughness, float occlusion, float3 emissive)
{
    roughness = applySpecularAntiAliasing(max(roughness, 0.04), N);
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

    // Emissive (texture/procedural for the stock path, anything for extensions) — HDR, blooms
    // in composite.
    color += emissive;

    // Highlight flash (selection/hit feedback): additive push toward white.
    color += draw.highlight.x * 0.35;

    // Return LINEAR HDR — tonemapping moved to the composite pass (composite.slang), which
    // tonemaps the whole HDR scene target once (enabling bloom + a single consistent curve).
    // The tone operators above are retained for reference/parity but are no longer called here.
    return float4(color, baseColor.a);
}

/// The stock surface path: sample the five material textures, apply the procedural override,
/// resolve the normal map, then hand the resolved surface to shadePbrCore. The built-in fragment
/// entry points call this unchanged.
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

    float occlusionSample = occlusionTexture.Sample(baseColorSampler, input.texcoord).r;
    float occlusion = lerp(1.0, occlusionSample, material.occlusionStrength);

    float3 emissive = emissiveTexture.Sample(baseColorSampler, input.texcoord).rgb * material.emissiveFactor.rgb
                    + procEmissive;

    return shadePbrCore(input, N, baseColor, metallic, roughness, occlusion, emissive);
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

#endif // PARADISE_PBR_CORE_SLANG
