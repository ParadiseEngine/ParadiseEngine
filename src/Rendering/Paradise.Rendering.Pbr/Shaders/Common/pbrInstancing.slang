#ifndef PARADISE_PBR_INSTANCING_SLANG
#define PARADISE_PBR_INSTANCING_SLANG

#include "pbrCore.slang"

[[vk::binding(1, 0)]]
StructuredBuffer<DrawUniforms> instanceDraws;

// Keep interpolation attributes on the entry-only wrapper: Slang otherwise emits an invalid
// @interpolate without @location on the internal FragmentInput used by shading helpers.
struct InstancedFragmentInput
{
    FragmentInput surface;
    nointerpolation uint instanceDrawIndex : INSTANCEDRAWINDEX;
};

// SV_InstanceID includes DrawIndexed.FirstInstance, preserving the packed frame's draw slot.
VertexOutput pbrTransformVertexInstanced(VertexInput input, uint instanceId)
{
    VertexOutput output = pbrTransformVertex(input.position, input.normal, input.texcoord,
        input.tangent, instanceDraws[instanceId]);
    output.instanceDrawIndex = instanceId;
    return output;
}

// Instanced fragments must not read the ordinary single-draw uniform for model-space effects.
DrawUniforms pbrInstanceDraw(uint instanceDrawIndex)
{
    return instanceDraws[instanceDrawIndex];
}

#endif // PARADISE_PBR_INSTANCING_SLANG
