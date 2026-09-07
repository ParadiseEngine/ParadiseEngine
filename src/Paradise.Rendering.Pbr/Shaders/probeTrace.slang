// probeTrace.slang — one ray per thread from each probe traced this frame into the scene's BVH.
// A hit is shaded with the frame's lights (Lambert, shadow-mapped like the raster path), the
// material's emissive, and the previous frame's probe irradiance for the bounce — which is what
// makes the bounce infinite: every frame carries last frame's light one step further. A miss
// samples the sky the raster ambient already uses, so a scene with nothing but sky converges to
// exactly the ambient it had. Results land in probeRays for the blend passes.
#define PARADISE_TRACE_GROUP 2
#include "Common/lighting.slang"
#include "Common/probes.slang"
#include "Common/bvh.slang"

// xyz: radiance along the ray, w: hit distance — negative for a back-face hit (the probe is
// inside something, which the relocation reads), MissDistance for a miss.
[[vk::binding(0, 0)]]
RWStructuredBuffer<float4> probeRays;

// Rays run until they hit something: a probe in a room must see the far wall, not the sky through
// it. Only the visibility blend clamps distances (to probeVolume.spacing.w), where a bounded
// range is what keeps a texel's variance meaningful.
static const float MissDistance = 1.0e5;

/// Sky radiance along a direction, in the scene's ambient convention (E/π-scaled colours,
/// exposure applied): the SH irradiance when the scene exports one, else the hemisphere zones.
float3 skyRadiance(float3 d)
{
    float exposure = max(frame.ambient.a, 0.0);
    if (frame.ambientGround.w > 0.5) return frame.ambient.rgb * exposure;
    if (frame.ambientSh[0].w > 0.5)
    {
        float3 sh = frame.ambientSh[0].rgb * 0.282095;
        sh += frame.ambientSh[1].rgb * (0.488603 * d.y);
        sh += frame.ambientSh[2].rgb * (0.488603 * d.z);
        sh += frame.ambientSh[3].rgb * (0.488603 * d.x);
        sh += frame.ambientSh[4].rgb * (1.092548 * d.x * d.y);
        sh += frame.ambientSh[5].rgb * (1.092548 * d.y * d.z);
        sh += frame.ambientSh[6].rgb * (0.315392 * (3.0 * d.z * d.z - 1.0));
        sh += frame.ambientSh[7].rgb * (1.092548 * d.x * d.z);
        sh += frame.ambientSh[8].rgb * (0.546274 * (d.x * d.x - d.y * d.y));
        return max(sh, 0.0) * exposure;
    }
    float hemisphere = d.y * 0.5 + 0.5;
    float3 color = hemisphere < 0.5
        ? lerp(frame.ambientGround.rgb, frame.ambientEquator.rgb, hemisphere * 2.0)
        : lerp(frame.ambientEquator.rgb, frame.ambient.rgb, (hemisphere - 0.5) * 2.0);
    return color * exposure;
}

/// Direct light on a Lambert surface at a hit, in the raster's non-physical convention (no 1/π;
/// light energy is the radiance multiplier), shadowed by the same maps the scene pass samples.
float3 directLight(float3 worldPos, float3 N, float2 noiseCoord)
{
    int count = min((int)frame.ambientEquator.w, MaxSceneLights);
    float3 sum = float3(0.0, 0.0, 0.0);
    for (int i = 0; i < count; i++)
    {
        SceneLight light = frame.sceneLights[i];
        float3 L;
        float attenuation = 1.0;
        if (light.positionAndType.w < 0.5)
        {
            L = normalize(light.directionAndRange.xyz);
        }
        else
        {
            float3 toLight = light.positionAndType.xyz - worldPos;
            float dist = length(toLight);
            L = toLight / max(dist, 0.0001);
            attenuation = getDistanceAttenuation(dist, light.directionAndRange.w, light.shadowAtlas.x);
            if (light.positionAndType.w > 1.5) attenuation *= getSpotAttenuation(light, L);
        }
        float ndotl = saturate(dot(N, L));
        if (ndotl <= 0.0 || attenuation <= 0.0) continue;
        float shadow = sampleSceneLightShadow(light, i, worldPos, N, L, noiseCoord);
        // sizeParams.z carries the light's indirect energy: how much of it the bounce carries.
        sum += light.colorAndIntensity.rgb * light.colorAndIntensity.w * light.sizeParams.z * attenuation * ndotl * shadow;
    }
    return sum;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void traceMain(uint3 id: SV_DispatchThreadID)
{
    int rays = (int)probeVolume.params.y;
    int windowProbe = (int)id.x / rays;
    int ray = (int)id.x % rays;
    if (windowProbe >= probeVolume.window.y) return;
    // The window wraps: a budget that does not divide the volume still visits every probe.
    int probe = (probeVolume.window.x + windowProbe) % probeVolume.counts.w;
    uint slot = (uint)(windowProbe * rays + ray);

    if (probeStates[probe].w < 0.5)
    {
        probeRays[slot] = float4(0.0, 0.0, 0.0, MissDistance);
        return;
    }

    float3 origin = probePosition(probe);
    float3 dir = probeRayDirection(ray, rays);

    TraceHit hit;
    if (!traceClosest(origin, dir, MissDistance, hit))
    {
        probeRays[slot] = float4(skyRadiance(dir), MissDistance);
        return;
    }

    // A back face means the probe sits inside geometry along this direction: no light, and the
    // distance is reported negative so the relocation can push the probe out.
    uint4 tri = traceTriangles[hit.triangle];
    float3 a = traceVertices[tri.x * 2].xyz;
    float3 b = traceVertices[tri.y * 2].xyz;
    float3 c = traceVertices[tri.z * 2].xyz;
    TraceInstance inst = traceInstances[hit.instance];
    float3 geometric = normalize(mul((float3x3)inst.normalMatrix, cross(b - a, c - a)));
    if (dot(geometric, dir) > 0.0)
    {
        probeRays[slot] = float4(0.0, 0.0, 0.0, -hit.t);
        return;
    }

    float3 hitPos = origin + dir * hit.t;
    float3 N = traceHitNormal(hit, dir);
    TraceMaterial material = traceMaterials[inst.material];
    // A metal has no diffuse: it bounces nothing the probes can carry (its specular would need a
    // reflection direction a probe does not have), so a chrome wall reads as dark to the probes.
    float3 albedo = material.baseColor.rgb * (1.0 - saturate(material.baseColor.a));
    float3 radiance = material.emissive.rgb;
    radiance += albedo * directLight(hitPos + N * 0.005, N, float2(ray, probe));
    // Last frame's probes light the hit point: the bounce. Outside the volume the sky stands in,
    // as it does for the raster ambient.
    float3 bounce;
    if (!sampleProbeIrradiance(hitPos, N, -dir, bounce)) bounce = skyRadiance(N);
    radiance += albedo * bounce;
    probeRays[slot] = float4(radiance, hit.t);
}
