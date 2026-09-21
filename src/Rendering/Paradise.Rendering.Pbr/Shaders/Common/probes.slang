// Shared group-3 probe uniforms, octahedral atlases, offsets and visibility-weighted irradiance
// lookup. Lookups use volume fields so future cascades can select another volume.
#ifndef PARADISE_PROBES_SLANG
#define PARADISE_PROBES_SLANG

struct ProbeVolume
{
    float4 origin;   // xyz: world position of probe (0,0,0), w: enabled (> 0.5)
    float4 spacing;  // xyz: distance between neighbouring probes, w: max ray distance
    int4 counts;     // xyz: probes per axis, w: total probes
    float4 atlas;    // x: irradiance interior texels per probe, y: visibility interior texels,
                     // z: 1/irradiance atlas width, w: 1/irradiance atlas height
    float4 atlas2;   // xy: 1/visibility atlas size, z: normal bias (fraction of spacing), w: view bias
    float4 params;   // x: intensity, y: rays per probe, z: hysteresis, w unused
    float4 rotation; // unit quaternion rotating this frame's ray directions
    int4 window;     // y: probes traced this frame, xzw unused
    int4 scroll;     // xyz: ring offsets mapping logical coordinates to physical storage
};

[[vk::binding(7, 3)]]
Texture2D<float4> probeIrradianceAtlas; // rgb: E/π per octahedral direction, with a 1-texel border

[[vk::binding(8, 3)]]
Texture2D<float4> probeVisibilityAtlas; // r: mean distance, g: mean squared distance, with border

[[vk::binding(9, 3)]]
ConstantBuffer<ProbeVolume> probeVolume;

// Per physical probe: xyz relocation, w: 1 active, 0 inactive, -1 awaiting its first trace.
[[vk::binding(10, 3)]]
StructuredBuffer<float4> probeStates;

[[vk::binding(11, 3)]]
SamplerState probeSampler; // linear, clamp: the borders make bilinear filtering wrap correctly

float2 signNotZero(float2 v)
{
    return float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
}

/// Unit direction → octahedral square in [-1, 1]².
float2 octEncode(float3 d)
{
    float l1 = abs(d.x) + abs(d.y) + abs(d.z);
    float2 uv = d.xy / l1;
    if (d.z < 0.0) uv = (1.0 - abs(uv.yx)) * signNotZero(uv);
    return uv;
}

/// Octahedral square in [-1, 1]² → unit direction.
float3 octDecode(float2 uv)
{
    float3 d = float3(uv, 1.0 - abs(uv.x) - abs(uv.y));
    if (d.z < 0.0) d.xy = (1.0 - abs(d.yx)) * signNotZero(d.xy);
    return normalize(d);
}

int3 probeCoords(int index)
{
    int3 c = probeVolume.counts.xyz;
    int3 physical = int3(index % c.x, (index / c.x) % c.y, index / (c.x * c.y));
    return (physical - probeVolume.scroll.xyz + c) % c;
}

int probeIndex(int3 coords)
{
    int3 c = probeVolume.counts.xyz;
    int3 physical = (coords + probeVolume.scroll.xyz) % c;
    return physical.x + c.x * (physical.y + c.y * physical.z);
}

/// The probe's world position: its grid point plus its relocation offset.
float3 probePosition(int index)
{
    int3 coords = probeCoords(index);
    return probeVolume.origin.xyz + probeVolume.spacing.xyz * float3(coords) + probeStates[index].xyz;
}

/// Tile origin (the first INTERIOR texel) of a probe in an atlas of `interior`-texel tiles: tiles
/// are laid out x-fastest then y along the atlas width, z along its height.
int2 probeTileOrigin(int3 coords, int interior)
{
    coords = (coords + probeVolume.scroll.xyz) % probeVolume.counts.xyz;
    int tile = interior + 2;
    return int2((coords.x + probeVolume.counts.x * coords.y) * tile + 1, coords.z * tile + 1);
}

/// Atlas uv of an octahedral direction inside a probe's tile, for bilinear sampling.
float2 probeAtlasUv(int3 coords, float2 oct, int interior, float2 invAtlasSize)
{
    float2 texel = float2(probeTileOrigin(coords, interior)) + (oct * 0.5 + 0.5) * (float)interior;
    return texel * invAtlasSize;
}

/// Spherical Fibonacci direction i of n, rotated by the frame's quaternion — the ray set both the
/// trace and the blend use, recomputed by each so no direction buffer travels between them.
float3 probeRayDirection(int ray, int rays)
{
    float phi = 6.2831853 * frac((float)ray * 0.6180339887);
    float cosTheta = 1.0 - 2.0 * ((float)ray + 0.5) / (float)rays;
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
    float3 d = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
    float4 q = probeVolume.rotation;
    // q * d * q⁻¹ for a unit quaternion.
    float3 t = 2.0 * cross(q.xyz, d);
    return d + q.w * t + cross(q.xyz, t);
}

/// Irradiance (E/π) at a surface from the eight probes around it: trilinear weights shaped by the
/// probe's direction relative to the normal (a probe behind the surface is not lit by what lights
/// the surface) and by Chebyshev visibility from the distance atlas (a probe behind a wall does
/// not leak through it). False where the volume is off, the point lies outside it, or every probe
/// around it is inactive — the caller falls back to the sky ambient there.
bool sampleProbeIrradiance(float3 worldPos, float3 N, float3 V, out float3 irradiance)
{
    irradiance = float3(0.0, 0.0, 0.0);
    if (probeVolume.origin.w < 0.5) return false;
    float3 spacing = probeVolume.spacing.xyz;
    // Bias the lookup off the surface, mostly toward the viewer: a texel-thin wall otherwise
    // samples probes on its far side.
    float3 biased = worldPos + (N * probeVolume.atlas2.z + V * probeVolume.atlas2.w) * spacing;

    float3 local = (biased - probeVolume.origin.xyz) / spacing;
    float3 extent = float3(probeVolume.counts.xyz - 1);
    // Half a cell of tolerance outside the outermost probes, then the sky takes over.
    if (any(local < -0.5) || any(local > extent + 0.5)) return false;
    int3 maxBase = probeVolume.counts.xyz - 2;
    int3 base = clamp((int3)floor(local), int3(0, 0, 0), maxBase);
    float3 alpha = saturate(local - float3(base));

    int irradianceTexels = (int)probeVolume.atlas.x;
    int visibilityTexels = (int)probeVolume.atlas.y;
    float2 invIrradiance = probeVolume.atlas.zw;
    float2 invVisibility = probeVolume.atlas2.xy;
    float2 octN = octEncode(N);

    float3 sum = float3(0.0, 0.0, 0.0);
    float weightSum = 0.0;
    for (int corner = 0; corner < 8; corner++)
    {
        int3 offset = int3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
        int3 coords = base + offset;
        int index = probeIndex(coords);
        float4 state = probeStates[index];
        if (state.w < 0.5) continue;

        float3 probePos = probeVolume.origin.xyz + spacing * float3(coords) + state.xyz;
        float3 trilinear = lerp(1.0 - alpha, alpha, float3(offset));
        float weight = max(trilinear.x * trilinear.y * trilinear.z, 1e-4);

        // Smooth back-face: a probe on the far side of the surface's tangent plane contributes
        // little; squared so the falloff is gentle rather than a cut.
        float3 toProbe = probePos - worldPos;
        float3 dirToProbe = normalize(toProbe);
        float wrap = (dot(dirToProbe, N) + 1.0) * 0.5;
        weight *= wrap * wrap + 0.2;

        // Chebyshev visibility from the biased point, against the probe's stored distances.
        float3 toProbeBiased = probePos - biased;
        float distToProbe = length(toProbeBiased);
        float2 octV = octEncode(-toProbeBiased / max(distToProbe, 1e-5));
        float2 moments = probeVisibilityAtlas.SampleLevel(probeSampler,
            probeAtlasUv(coords, octV, visibilityTexels, invVisibility), 0.0).rg;
        float mean = moments.x;
        float variance = abs(moments.x * moments.x - moments.y);
        if (distToProbe > mean)
        {
            float d = distToProbe - mean;
            float chebyshev = variance / (variance + d * d);
            weight *= max(chebyshev * chebyshev * chebyshev, 0.0);
        }
        weight = max(weight, 1e-6);

        float3 probeIrradiance = probeIrradianceAtlas.SampleLevel(probeSampler,
            probeAtlasUv(coords, octN, irradianceTexels, invIrradiance), 0.0).rgb;
        sum += probeIrradiance * weight;
        weightSum += weight;
    }
    if (weightSum <= 0.0) return false;
    irradiance = sum / weightSum * probeVolume.params.x;
    return true;
}

#endif // PARADISE_PROBES_SLANG
