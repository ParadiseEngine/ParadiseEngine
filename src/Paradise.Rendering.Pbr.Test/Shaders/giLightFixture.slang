#include "Common/giLights.slang"

[[vk::binding(0, 0)]] RWStructuredBuffer<uint2> masks;
[[vk::binding(2, 0)]] StructuredBuffer<float4> positions;

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 id : SV_DispatchThreadID)
{
    masks[id.x] = giLightMask(positions[id.x].xyz);
}
