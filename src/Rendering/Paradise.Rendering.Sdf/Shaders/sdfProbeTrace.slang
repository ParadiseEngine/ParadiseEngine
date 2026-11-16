// Traces this frame's rays for the irradiance probes: one texel per ray, a row per probe. Each
// ray returns the light arriving at the probe from its direction, the sky or a surface lit
// directly and, through last frame's probes, indirectly, so every frame adds a bounce.
// Alpha is the probe's validity: 0 inside a shape, 1 at a clear distance from every surface.

#define SDF_PROBES 1
#include "Common/sdfField.slang"

struct Varyings
{
    float4 position : SV_Position;
};

[shader("vertex")]
Varyings vertexMain(uint vertexId : SV_VertexID)
{
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    int ray = (int)input.position.x;
    int probe = (int)input.position.y;
    int rays = (int)sdfGi.params.w;
    int countX = (int)sdfGi.counts.x;
    int countZ = (int)sdfGi.counts.z;
    float3 cell = float3((float)(probe % countX), (float)(probe / (countX * countZ)), (float)((probe / countX) % countZ));
    float3 origin = sdfGi.origin.xyz + cell * sdfGi.origin.w;

    // One sample that belongs to no ray: fold every shape.
    gCandidateCount = -1;
    gPlanesFrom = 0.0;
    float validity = saturate(sceneDistance(origin) / max(sdfGi.update.z, 1e-4));
    if (validity <= 0.0) return float4(0.0, 0.0, 0.0, 0.0);

    float3 direction = sdfGiRayDirection(ray, rays, sdfGi.rayRotation);
    float bounce = sdfGi.update.w;
    float3 radiance = sdfSecondaryRadiance(origin, direction, sdfGi.update.y, SDF_PROBE_STEPS, true, bounce > 0.0, bounce);
    // Bounded so that a moment, three times a mean, still fits the atlas's half floats: a probe
    // that reached infinity would never come back from it.
    return float4(min(radiance, float3(1e4, 1e4, 1e4)), validity);
}
