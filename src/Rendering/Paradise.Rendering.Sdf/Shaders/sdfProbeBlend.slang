// Folds this frame's probe rays into the probe atlas. Each probe owns SDF_PROBE_TEXELS texels:
// the mean radiance around it, three times the mean of radiance x direction along x, y and z,
// and its validity. Light is blended with last frame's atlas so few rays a frame converge over
// many; validity follows the field at once.

#include "Common/sdfGi.slang"

[[vk::binding(0, 0)]] Texture2D<float4> probeRays;
[[vk::binding(1, 0)]] Texture2D<float4> probeHistory;
[[vk::binding(2, 0)]] ConstantBuffer<SdfGiUniforms> sdfGi;

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
    int2 texel = int2(input.position.xy);
    int probe = texel.y * (int)sdfGi.counts.w + texel.x / SDF_PROBE_TEXELS;
    int coefficient = texel.x % SDF_PROBE_TEXELS;
    int probes = (int)(sdfGi.counts.x * sdfGi.counts.y * sdfGi.counts.z);
    if (probe >= probes) return float4(0.0, 0.0, 0.0, 0.0);

    float validity = probeRays.Load(int3(0, probe, 0)).a;
    if (coefficient == 4) return float4(validity, validity, validity, 1.0);
    // A probe inside a shape traced nothing and holds nothing.
    if (validity <= 0.0) return float4(0.0, 0.0, 0.0, 1.0);

    int rays = (int)sdfGi.params.w;
    float3 axis = float3(coefficient == 1 ? 1.0 : 0.0, coefficient == 2 ? 1.0 : 0.0, coefficient == 3 ? 1.0 : 0.0);
    float3 sum = float3(0.0, 0.0, 0.0);
    for (int ray = 0; ray < rays; ray++)
    {
        float3 radiance = probeRays.Load(int3(ray, probe, 0)).rgb;
        float weight = coefficient == 0 ? 1.0 : 3.0 * dot(sdfGiRayDirection(ray, rays, sdfGi.rayRotation), axis);
        sum += radiance * weight;
    }
    float3 current = sum / (float)rays;
    float3 history = probeHistory.Load(int3(texel, 0)).rgb;
    return float4(lerp(history, current, sdfGi.update.x), 1.0);
}
