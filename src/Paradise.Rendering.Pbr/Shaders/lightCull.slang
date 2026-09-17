// Forward+ binning: each thread tests light spheres against one view-space froxel and owns its two
// mask words, requiring no atomics.
// Upload only light centers/radii; including lighting.slang would add unused mandatory bindings.
// Upload CPU slice boundaries too: WGSL pow rounding can differ from MathF.Pow and change boundary
// masks.

struct CullUniforms
{
    float4 origin; // xy: NDC scale, zw: offset
    float4 depth;  // xy: NDC scale per view depth, zw: offset per view depth
    float4 params;  // x: near, y: far, z: tile size in pixels, w: light count in cullLights
    float4 screen;  // x: width, y: height, z: tilesX, w: tilesY
    float4 grid;    // x: zSlices, y: froxel count, zw unused
};

// One point or spot light as binning sees it. Directional lights are absent rather than skipped:
// they are never clustered, so leaving them out of the array costs the shader no branch.
struct CullLight
{
    float4 viewPositionRange; // xyz: view-space centre, w: range
    uint4 slot;               // x: index in frame.sceneLights — the bit this light owns
};

[[vk::binding(0, 0)]]
ConstantBuffer<CullUniforms> cull;

[[vk::binding(1, 0)]]
StructuredBuffer<CullLight> cullLights;

[[vk::binding(2, 0)]]
RWStructuredBuffer<uint> clusterMasksOut;

// zSlices + 1 positive view distances: the near and far plane of every slice.
[[vk::binding(3, 0)]]
StructuredBuffer<float> sliceDepths;

float2 ndcCorner(float pixelX, float pixelY)
{
    float ndcX = pixelX / cull.screen.x * 2.0 - 1.0;
    float ndcY = 1.0 - pixelY / cull.screen.y * 2.0;
    return float2(ndcX, ndcY);
}

// Same depth-parametric mapping as ViewDepthMapping.AtDepth, for both projection types.
float3 atDepth(float2 ndc, float depth)
{
    float2 origin = ndc * cull.origin.xy + cull.origin.zw;
    float2 slope = ndc * cull.depth.xy + cull.depth.zw;
    return float3(origin + depth * slope, -depth);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void lightCullMain(uint3 id: SV_DispatchThreadID)
{
    uint froxel = id.x;
    if (froxel >= (uint)cull.grid.y) return;

    int tilesX = (int)cull.screen.z;
    int tilesY = (int)cull.screen.w;
    int tileX = (int)(froxel % (uint)tilesX);
    int tileY = (int)((froxel / (uint)tilesX) % (uint)tilesY);
    int slice = (int)(froxel / (uint)(tilesX * tilesY));

    // The tile's pixel rect, clamped to the frame: the last tile in a row is a partial one, and
    // letting it claim a full tile would bound lights into froxels no pixel occupies.
    float tileSize = cull.params.z;
    float x0 = (float)tileX * tileSize;
    float y0 = (float)tileY * tileSize;
    float x1 = min(x0 + tileSize, cull.screen.x);
    float y1 = min(y0 + tileSize, cull.screen.y);

    float2 corner00 = ndcCorner(x0, y0);
    float2 corner11 = ndcCorner(x1, y1);
    float near = sliceDepths[slice];
    float far = sliceDepths[slice + 1];
    // Validated projections have no XY mixing, so these diagonals contain every axis extremum.
    float3 near00 = atDepth(corner00, near);
    float3 near11 = atDepth(corner11, near);
    float3 far00 = atDepth(corner00, far);
    float3 far11 = atDepth(corner11, far);
    float3 aabbMin = min(min(near00, near11), min(far00, far11));
    float3 aabbMax = max(max(near00, near11), max(far00, far11));

    uint mask0 = 0u;
    uint mask1 = 0u;
    int count = (int)cull.params.w;
    for (int i = 0; i < count; i++)
    {
        CullLight light = cullLights[i];
        float3 closest = clamp(light.viewPositionRange.xyz, aabbMin, aabbMax);
        float3 delta = light.viewPositionRange.xyz - closest;
        float range = light.viewPositionRange.w;
        if (dot(delta, delta) > range * range) continue;
        uint bit = light.slot.x;
        if (bit < 32u) mask0 |= 1u << bit;
        else mask1 |= 1u << (bit - 32u);
    }

    // Both words are written unconditionally: nothing clears this buffer between frames, so a
    // froxel no light reaches must be told it is empty rather than keep the last frame's answer.
    clusterMasksOut[froxel * 2u] = mask0;
    clusterMasksOut[froxel * 2u + 1u] = mask1;
}
