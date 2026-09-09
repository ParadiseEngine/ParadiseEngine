#ifndef PARADISE_GI_LIGHTS
#define PARADISE_GI_LIGHTS

// Matches GiLightHierarchy: a preorder BVH of finite-range lights. Directionals and local
// lights with no range cutoff live in the global masks and remain visible at offscreen hits.
struct GiLightNode
{
    float3 minBounds;
    uint escape;
    float3 maxBounds;
    uint light;
};

struct GiLightTree
{
    uint nodeCount;
    uint globalMask0;
    uint globalMask1;
    uint padding;
    GiLightNode nodes[127];
};

[[vk::binding(1, 0)]]
ConstantBuffer<GiLightTree> giLightTree;

uint2 giLightMask(float3 position)
{
    uint2 mask = uint2(giLightTree.globalMask0, giLightTree.globalMask1);
    uint index = 0;
    while (index < giLightTree.nodeCount)
    {
        GiLightNode node = giLightTree.nodes[index];
        if (any(position < node.minBounds) || any(position > node.maxBounds))
        {
            index = node.escape;
            continue;
        }
        if (node.light != 0xffffffffu)
        {
            if (node.light < 32) mask.x |= 1u << node.light;
            else mask.y |= 1u << (node.light - 32);
        }
        index++;
    }
    return mask;
}

#endif
