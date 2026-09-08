// Forward+ binning: each thread tests light spheres against one view-space froxel and owns its two
// mask words, requiring no atomics.
// Upload only light centers/radii; including lighting.slang would add unused mandatory bindings.
// Upload CPU slice boundaries too: WGSL pow rounding can differ from MathF.Pow and change boundary
// masks.

struct CullUniforms
{
    // Inverse of the camera PROJECTION (not view-projection): unprojects a tile corner at NDC
    // z = 0 into a view-space point on the near plane. Uploaded like every other matrix here —
    // raw System.Numerics bytes, read through mul(M, v); see PbrUniforms.cs on the duality.
    float4x4 invProjection;
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

// A tile corner in view space, on the near plane. The camera sits at the origin looking down -Z,
// so this doubles as the direction of the ray through that corner.
float3 unprojectCorner(float pixelX, float pixelY)
{
    float ndcX = pixelX / cull.screen.x * 2.0 - 1.0;
    float ndcY = 1.0 - pixelY / cull.screen.y * 2.0;
    float4 view = mul(cull.invProjection, float4(ndcX, ndcY, 0.0, 1.0));
    return view.xyz / view.w;
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

    float3 corner00 = unprojectCorner(x0, y0);
    float3 corner10 = unprojectCorner(x1, y0);
    float3 corner01 = unprojectCorner(x0, y1);
    float3 corner11 = unprojectCorner(x1, y1);

    // Scale each near-plane corner out to the slice's two depths. A corner sits at view z = -near,
    // so scaling by (sliceDepth / near) lands it at view z = -sliceDepth.
    float near = cull.params.x;
    float scaleNear = sliceDepths[slice] / near;
    float scaleFar = sliceDepths[slice + 1] / near;

    float3 aabbMin = corner00 * scaleNear;
    float3 aabbMax = aabbMin;
    float3 points[8] = {
        corner00 * scaleNear, corner10 * scaleNear, corner01 * scaleNear, corner11 * scaleNear,
        corner00 * scaleFar,  corner10 * scaleFar,  corner01 * scaleFar,  corner11 * scaleFar,
    };
    for (int c = 0; c < 8; c++)
    {
        aabbMin = min(aabbMin, points[c]);
        aabbMax = max(aabbMax, points[c]);
    }

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
