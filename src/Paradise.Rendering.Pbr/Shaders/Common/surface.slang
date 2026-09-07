// surface.slang — how the STOCK surface is resolved: the five material textures, the
// KHR_texture_transform on the baseColor slot, the procedural override, and the normal map.
//
// The layer pbrCore.slang used to keep welded shut. `shadePbr` sampled, overrode, normal-mapped
// and lit in one body, so a game shader that wanted the stock surface and then ONE change to it —
// a burn-edge emissive, a wetness tint, a coverage cut — had to copy that body into its own file
// and re-copy it whenever the engine's changed. `PbrSurface` is that intermediate value given a
// name, and the reason this file exists:
//
//   PbrSurface s = resolvePbrSurface(input);   // the stock surface, sampled exactly as the engine does
//   s.emissive += burn;                        // the game's one change
//   return shadeSurface(input, s);             // the stock lighting, unchanged
//
// Two seams instead of one, and the middle value is now addressable.
#ifndef PARADISE_PBR_SURFACE_SLANG
#define PARADISE_PBR_SURFACE_SLANG

#include "uniforms.slang"
#include "procedural.slang"
#include "shading.slang"

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

/// A resolved surface, ready to light: what the stock texture-sampling path produces and what
/// <c>shadePbrCore</c> consumes. The fields are exactly its parameters, in its order.
struct PbrSurface
{
    float3 N;         // world-space shading normal (unit), normal-mapped and front-face corrected
    float4 baseColor; // rgba albedo (a = coverage for blend materials)
    float  metallic;
    float  roughness;
    float  occlusion; // baked/texture occlusion, 1 for none (SSAO is applied on top, in shading)
    float3 emissive;  // HDR, added after lighting (blooms in composite)
};

/// The stock surface path: sample the five material textures, apply the procedural override, and
/// resolve the normal map. Everything <c>shadePbr</c> used to do except the lighting call.
PbrSurface resolvePbrSurface(FragmentInput input)
{
    PbrSurface surface;

    float2 baseUv = transformBaseColorUv(input.texcoord);
    float4 baseColorSample = baseColorTexture.Sample(baseColorSampler, baseUv);
    surface.baseColor = baseColorSample * material.baseColorFactor;

    float4 metallicRoughnessSample = metallicRoughnessTexture.Sample(baseColorSampler, input.texcoord);
    surface.metallic = saturate(material.metallicFactor * metallicRoughnessSample.b);
    surface.roughness = max(material.roughnessFactor * metallicRoughnessSample.g, 0.04); // clamp to avoid division by zero

    // Procedural material: override the sampled surface from a noise recipe (object-space, animated
    // by frame.time). The result then flows through the normal PBR lighting.
    float3 procEmissive = float3(0.0, 0.0, 0.0);
    int procKind = int(material.procParams.x + 0.5);
    if (procKind >= 1)
    {
        ProcResult pr = evalProcedural(
            procKind, input.objectPos * material.procParams.y, frame.time.x * material.procParams.z,
            material.procColorA.rgb, material.procColorB.rgb);
        surface.baseColor = float4(pr.albedo, surface.baseColor.a);
        surface.roughness = max(pr.roughness, 0.04);
        surface.metallic = pr.metallic;
        procEmissive = pr.emissive * material.procParams.w;
    }

    // Back-facing fragments shade with the negated geometric normal (Godot's double-sided
    // convention: `normal_interp * (FRONT_FACING ? 1.0 : -1.0)` before normal mapping).
    float3 geometricNormal = input.isFrontFace ? input.worldNormal : -input.worldNormal;
    surface.N = sampleNormal(input.texcoord, geometricNormal, input.worldTangent);

    float occlusionSample = occlusionTexture.Sample(baseColorSampler, input.texcoord).r;
    surface.occlusion = lerp(1.0, occlusionSample, material.occlusionStrength);

    surface.emissive = emissiveTexture.Sample(baseColorSampler, input.texcoord).rgb * material.emissiveFactor.rgb
                     + procEmissive;

    return surface;
}

/// <c>shadePbrCore</c> over a resolved surface. The struct-shaped call, so a game shader that
/// modified one field does not have to restate the other six in the right order.
float4 shadeSurface(FragmentInput input, PbrSurface surface)
{
    return shadePbrCore(input, surface.N, surface.baseColor, surface.metallic,
        surface.roughness, surface.occlusion, surface.emissive);
}

#endif // PARADISE_PBR_SURFACE_SLANG
