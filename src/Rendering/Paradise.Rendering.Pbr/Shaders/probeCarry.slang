// The two atlas/state sets differ only at the previous update selection. Carry those probes
// before blending this frame so untouched probes survive without a full-atlas dispatch.
#include "Common/probes.slang"

[[vk::binding(0, 0)]]
StructuredBuffer<int> probeCarryIndices;

[[vk::binding(1, 0)]]
[format("rgba16f")]
WTexture2D<float4> irradianceOut;

[[vk::binding(2, 0)]]
[format("rgba16f")]
WTexture2D<float4> visibilityOut;

[[vk::binding(3, 0)]]
RWStructuredBuffer<float4> probeStatesOut;

[shader("compute")]
[numthreads(10, 10, 1)]
void carryIrradiance(uint3 groupId: SV_GroupID, uint3 threadId: SV_GroupThreadID)
{
    int probe = probeCarryIndices[groupId.x];
    int2 texel = probeTileOrigin(probeCoords(probe), 8) - 1 + int2(threadId.xy);
    irradianceOut.Store(texel, probeIrradianceAtlas.Load(int3(texel, 0)));
    if (threadId.x == 0 && threadId.y == 0) probeStatesOut[probe] = probeStates[probe];
}

[shader("compute")]
[numthreads(16, 16, 1)]
void carryVisibility(uint3 groupId: SV_GroupID, uint3 threadId: SV_GroupThreadID)
{
    int probe = probeCarryIndices[groupId.x];
    int2 texel = probeTileOrigin(probeCoords(probe), 14) - 1 + int2(threadId.xy);
    visibilityOut.Store(texel, probeVisibilityAtlas.Load(int3(texel, 0)));
}
