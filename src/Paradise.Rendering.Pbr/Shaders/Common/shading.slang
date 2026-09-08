// PBR lighting and tone math. shadePbrCore accepts a resolved normal and material factors, allowing
// custom surfaces to use stock lighting independently of surface.slang sampling.
#ifndef PARADISE_PBR_SHADING_SLANG
#define PARADISE_PBR_SHADING_SLANG

#include "uniforms.slang"
#include "probes.slang"
#include "tonemapping.slang"

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
    // Both terms are bounded by 1 (a lossless split sum). The table's grazing column divides by
    // NdotV and is stored in fp16, so a silhouette pixel could read infinity here — and one
    // infinite pixel through the bloom chain is a white frame.
    return saturate(dfgLut.SampleLevel(skySpecularSampler, float2(NdotV, roughness), 0.0).rg);
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

float3 evaluateDirectLight(
    SceneLight light,
    int lightIndex,
    float3 worldPos,
    float3 N,
    float3 geoN,
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

    // Godot size_A widens lighting cosines by physical light size. Directional lights precompute
    // 1-cos(angle); point/spot use max(1-1/sqrt(1+(radius/distance)^2),0).
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
    // Match Godot's non-physical light units: Lambert omits 1/pi; specular retains pi scaled by
    // shadowAtlas.z. Recheck pixel parity before changing either scale.
    // GGX uses alpha=roughness^2, Hammon visibility 0.5/mix(2*NL*NV,NL+NV,alpha) (already includes
    // Smith and the denominator), and Schlick f90=clamp(dot(f0,16.67),metallic,1).
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
    float shadow = sampleSceneLightShadow(light, lightIndex, worldPos, geoN, L, fragCoord);
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

// Screen-space ambient occlusion from the depth pre-pass. A spiral of screen-space taps around the
// fragment; each reconstructs the neighbour's world position from its depth and occludes when it's
// within `radius` of the fragment and above its tangent plane. Returns a [0,1] AO multiplier
// applied to ambient only.
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

        float4 p = prepassWorldPosition(samplePix, screenSize);
        if (p.w < 0.5) continue; // background (depth at the clear value)

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
    // The GEOMETRIC normal, for the shadow-bias geometry only (see sampleSceneLightShadow):
    // the front-face-corrected vertex normal, independent of whatever normal map shaped N.
    float3 geoN = normalize(input.isFrontFace ? input.worldNormal : -input.worldNormal);

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
            geoN,
            V,
            F0,
            baseColor.rgb,
            metallic,
            roughness,
            energyComp,
            input.position.xy);
    }

    // Ambient: L2 SH sky irradiance per-normal (Godot's sky-SH ambient) when provided; the
    // 3-zone hemisphere blend remains as the fallback, and the flat mode overrides both. With a
    // probe volume on, the probes replace all three for surfaces inside it (their misses already
    // carry the sky), except for a draw whose GI mode is disabled (highlight.z).
    float3 hemisphereColor;
    float3 probeIrradiance;
    bool probeLit = false;
    if (probeVolume.origin.w > 0.5 && draw.highlight.z < 0.5
        && sampleProbeIrradiance(input.worldPos, N, V, probeIrradiance))
    {
        hemisphereColor = probeIrradiance;
        probeLit = true;
    }
    else if (frame.ambientGround.w > 0.5)
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
    // Screen-space and ray-traced AO also darken only the ambient, like Godot.
    occlusion *= computeSsao(input.position.xy, input.worldPos, N);
    // Sampled, not loaded: the AO target may be a fraction of the frame's size, and bilinear
    // filtering is the upsample.
    if (ssao.rtao.x > 0.5)
        occlusion *= rtAoTexture.SampleLevel(skySpecularSampler, input.position.xy * ssao.screen.xy, 0.0).r;
    // The probes already carry the ambient exposure: their sky term applied it in the trace and
    // their bounce term must not have it. Scaling here again would make the sky share of the
    // indirect light exposure² while the bounce share stays exposure¹.
    float exposure = probeLit ? 1.0 : max(frame.ambient.a, 0.0);
    float3 ambient = hemisphereColor * exposure * baseColor.rgb * (1.0 - metallic) * occlusion;

    // Probe irradiance approximates reflected radiance: accurate for rough surfaces, blurred for
    // polished ones. This prevents unlit metals inside rooms; use sky LUTs outside the volume.
    float f90i = clamp(50.0 * F0.g, metallic, 1.0);
    float3 R = reflect(-V, N);
    // Screen-space reflection first: where it is confident it replaces the environment lookups
    // below, and a rough surface fades it out toward them. A fragment in front of the pre-pass
    // surface (a blended one) is not the surface the ray was traced from, so it takes none.
    float ssrWeight = 0.0;
    float3 ssrColor = float3(0.0, 0.0, 0.0);
    if (ssao.rtao.y > 0.5)
    {
        float2 screenUv = input.position.xy * ssao.screen.xy;
        float4 ssrSample = ssrTexture.SampleLevel(skySpecularSampler, screenUv, 0.0);
        float opaqueDepth = prepassDepthTexture.Load(int3(int2(input.position.xy), 0)).r;
        float onSurface = input.position.z >= opaqueDepth - 1e-4 ? 1.0 : 0.0;
        ssrWeight = ssrSample.a * saturate(1.0 - roughness / max(ssao.rtao.z, 1e-3)) * onSurface;
        ssrColor = ssrSample.rgb;
    }
    float3 reflected;
    if (probeVolume.origin.w > 0.5 && draw.highlight.z < 0.5
        && sampleProbeIrradiance(input.worldPos, R, V, reflected))
    {
        ambient += reflected * energyComp * (F0 * envAb.x + f90i * envAb.y) * occlusion * (1.0 - ssrWeight);
    }
    // Split-sum specular uses gradient LUT rows 0..7 and sun rows 8..15 (stored /4 for HDR
    // headroom). The first directional light supplies the sun; disabling it removes its reflection.
    // AO affects both specular and diffuse ambient.
    else if (frame.aaSettings.x > 0.5)
    {
        float NdotV = clamp(dot(N, V), 0.0001, 1.0);
        float vHalf = (clamp(roughness, 0.0, 1.0) * 7.0 + 0.5) / 16.0;
        // SampleLevel, not Sample: this branch now sits under a per-fragment condition (whether
        // the probes answered), and WGSL forbids an implicit-derivative sample in non-uniform
        // control flow — the module fails to compile and Dawn drops the whole frame. The LUT has
        // one mip, so the explicit level costs nothing.
        float3 prefiltered = skySpecularLut.SampleLevel(skySpecularSampler, float2(R.y * 0.5 + 0.5, vHalf), 0.0).rgb;
        for (int li = 0; li < sceneLightCount; li++)
        {
            if (frame.sceneLights[li].positionAndType.w < 0.5)
            {
                float cosToSun = dot(R, normalize(frame.sceneLights[li].directionAndRange.xyz));
                prefiltered += skySpecularLut.SampleLevel(skySpecularSampler,
                    float2(cosToSun * 0.5 + 0.5, 0.5 + vHalf), 0.0).rgb * 4.0;
                break;
            }
        }
        // Godot's indirect-specular combine (scene_forward_clustered.glsl): the split-sum pair
        // with the cheap-luminance f90 and the multiscatter energy compensation (envAb/energyComp
        // computed once per fragment above).
        ambient += prefiltered * energyComp * (F0 * envAb.x + f90i * envAb.y) * occlusion * (1.0 - ssrWeight);
    }
    ambient += ssrColor * ssrWeight * energyComp * (F0 * envAb.x + f90i * envAb.y) * occlusion;

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

#endif // PARADISE_PBR_SHADING_SLANG
