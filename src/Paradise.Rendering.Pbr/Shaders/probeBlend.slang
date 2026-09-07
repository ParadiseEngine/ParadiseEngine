// probeBlend.slang — fold this frame's rays into the probe atlases. One workgroup per probe:
// the interior threads integrate the rays for their octahedral texel and blend it against the
// previous atlas with hysteresis; the border threads then copy the octahedral wrap from the
// interior so bilinear filtering reads across a tile edge correctly. Two entry points share the
// shape: irradiance (8-texel interior) and visibility (14-texel interior, mean and mean-squared
// distance, so a lookup can ask Chebyshev's inequality whether a probe is behind a wall).
//
// Probes outside this frame's trace window carry their old texel forward unchanged, which is what
// lets a frame trace fewer probes than the volume holds.
#include "Common/probes.slang"

[[vk::binding(0, 0)]]
StructuredBuffer<float4> probeRays;

[[vk::binding(1, 0)]]
[format("rgba16f")]
WTexture2D<float4> irradianceOut;

[[vk::binding(2, 0)]]
[format("rgba16f")]
WTexture2D<float4> visibilityOut;

static const int IrradianceTexels = 8;
static const int VisibilityTexels = 14;
// Must match ProbeGiFeature.MaxRaysPerProbe: the staging arrays below are sized by it.
static const int MaxRays = 256;

groupshared float4 s_irradiance[IrradianceTexels + 2][IrradianceTexels + 2];
groupshared float4 s_visibility[VisibilityTexels + 2][VisibilityTexels + 2];
// This probe's rays, staged once per workgroup: every texel thread integrates the same rays, and
// reading them from the buffer per texel — and recomputing each direction's trig per texel — was
// the blend's whole cost.
groupshared float4 s_rays[MaxRays];
groupshared float3 s_rayDirs[MaxRays];

/// Stage the probe's rays and directions into workgroup memory; `threads` cooperate.
void stageRays(int windowProbe, int rays, int thread, int threads)
{
    for (int ray = thread; ray < rays; ray += threads)
    {
        s_rays[ray] = probeRays[windowProbe * rays + ray];
        s_rayDirs[ray] = probeRayDirection(ray, rays);
    }
}

/// The interior texel a border texel mirrors, for a tile of `n` interior texels: octahedral maps
/// wrap edge-to-opposite-edge reversed, and corners to the diagonally opposite interior corner.
int2 borderSource(int2 t, int n)
{
    bool left = t.x == 0, right = t.x == n + 1, top = t.y == 0, bottom = t.y == n + 1;
    if ((left || right) && (top || bottom))
        return int2(left ? n : 1, top ? n : 1);
    if (left) return int2(1, n + 1 - t.y);
    if (right) return int2(n, n + 1 - t.y);
    if (top) return int2(n + 1 - t.x, 1);
    return int2(n + 1 - t.x, n);
}

bool probeTracedThisFrame(int probe, out int windowProbe)
{
    windowProbe = probe - probeVolume.window.x;
    return windowProbe >= 0 && windowProbe < probeVolume.window.y;
}

[shader("compute")]
[numthreads(IrradianceTexels + 2, IrradianceTexels + 2, 1)]
void blendIrradiance(uint3 groupId: SV_GroupID, uint3 threadId: SV_GroupThreadID)
{
    int probe = (int)groupId.x;
    int2 t = int2(threadId.xy);
    int3 coords = probeCoords(probe);
    int2 tileOrigin = probeTileOrigin(coords, IrradianceTexels) - 1;
    bool interior = t.x >= 1 && t.x <= IrradianceTexels && t.y >= 1 && t.y <= IrradianceTexels;

    int windowProbe;
    bool traced = probeTracedThisFrame(probe, windowProbe);
    int rays = (int)probeVolume.params.y;
    if (traced) stageRays(windowProbe, rays, t.y * (IrradianceTexels + 2) + t.x, (IrradianceTexels + 2) * (IrradianceTexels + 2));
    GroupMemoryBarrierWithGroupSync();
    if (interior)
    {
        float4 previous = probeIrradianceAtlas.Load(int3(tileOrigin + t, 0));
        float4 result = previous;
        if (traced)
        {
            float2 oct = (float2(t - 1) + 0.5) / (float)IrradianceTexels * 2.0 - 1.0;
            float3 texelDir = octDecode(oct);
            float3 sum = float3(0.0, 0.0, 0.0);
            float weightSum = 0.0;
            for (int ray = 0; ray < rays; ray++)
            {
                float4 sample = s_rays[ray];
                if (sample.w < 0.0) continue; // back-face hits carry no light
                float weight = max(0.0, dot(texelDir, s_rayDirs[ray]));
                sum += sample.rgb * weight;
                weightSum += weight;
            }
            float3 irradiance = weightSum > 1e-4 ? sum / weightSum : float3(0.0, 0.0, 0.0);
            float hysteresis = probeVolume.params.z;
            result = float4(lerp(irradiance, previous.rgb, hysteresis), 1.0);
        }
        s_irradiance[t.y][t.x] = result;
    }
    GroupMemoryBarrierWithGroupSync();
    float4 value = interior ? s_irradiance[t.y][t.x] : s_irradiance[borderSource(t, IrradianceTexels).y][borderSource(t, IrradianceTexels).x];
    irradianceOut.Store(tileOrigin + t, value);
}

[shader("compute")]
[numthreads(VisibilityTexels + 2, VisibilityTexels + 2, 1)]
void blendVisibility(uint3 groupId: SV_GroupID, uint3 threadId: SV_GroupThreadID)
{
    int probe = (int)groupId.x;
    int2 t = int2(threadId.xy);
    int3 coords = probeCoords(probe);
    int2 tileOrigin = probeTileOrigin(coords, VisibilityTexels) - 1;
    bool interior = t.x >= 1 && t.x <= VisibilityTexels && t.y >= 1 && t.y <= VisibilityTexels;

    int windowProbe;
    bool traced = probeTracedThisFrame(probe, windowProbe);
    int rays = (int)probeVolume.params.y;
    if (traced) stageRays(windowProbe, rays, t.y * (VisibilityTexels + 2) + t.x, (VisibilityTexels + 2) * (VisibilityTexels + 2));
    GroupMemoryBarrierWithGroupSync();
    if (interior)
    {
        float4 previous = probeVisibilityAtlas.Load(int3(tileOrigin + t, 0));
        float4 result = previous;
        if (traced)
        {
            float maxDistance = probeVolume.spacing.w;
            float2 oct = (float2(t - 1) + 0.5) / (float)VisibilityTexels * 2.0 - 1.0;
            float3 texelDir = octDecode(oct);
            float2 sum = float2(0.0, 0.0);
            float weightSum = 0.0;
            for (int ray = 0; ray < rays; ray++)
            {
                float4 sample = s_rays[ray];
                // A back face is a wall the probe is inside of: shortened, so the probe reads as
                // occluded there without its variance exploding.
                float distance = sample.w < 0.0 ? -sample.w * 0.2 : sample.w;
                distance = min(distance, maxDistance);
                // Sharpened so each texel sees only rays near its direction; the wider
                // irradiance lobe would blur depth edges into leaks.
                float weight = pow(max(0.0, dot(texelDir, s_rayDirs[ray])), 50.0);
                sum += float2(distance, distance * distance) * weight;
                weightSum += weight;
            }
            float2 moments = weightSum > 1e-6 ? sum / weightSum : float2(maxDistance, maxDistance * maxDistance);
            float hysteresis = probeVolume.params.z;
            result = float4(lerp(moments, previous.rg, hysteresis), 0.0, 1.0);
        }
        s_visibility[t.y][t.x] = result;
    }
    GroupMemoryBarrierWithGroupSync();
    float4 value = interior ? s_visibility[t.y][t.x] : s_visibility[borderSource(t, VisibilityTexels).y][borderSource(t, VisibilityTexels).x];
    visibilityOut.Store(tileOrigin + t, value);
}
