struct OcclusionUniforms { float4 screen; float4 counts; };
struct DrawBounds { float4 rectangle; float nearest; uint indexCount; uint visible; uint projected; };
[[vk::binding(0, 0)]] ConstantBuffer<OcclusionUniforms> occlusion;
[[vk::binding(1, 0)]] StructuredBuffer<DrawBounds> bounds;
[[vk::binding(2, 0)]] StructuredBuffer<float> depthTiles;
[[vk::binding(3, 0)]] RWStructuredBuffer<uint> indirectArgs;
[shader("compute")]
[numthreads(64, 1, 1)]
void cullDraws(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)occlusion.counts.x) return;
    DrawBounds b = bounds[id.x];
    bool visible = b.visible != 0;
    if (visible && b.projected != 0)
    {
        visible = false;
        uint4 tile = (uint4)b.rectangle / 8;
        for (uint y = tile.y; y <= tile.w; y++)
        for (uint x = tile.x; x <= tile.z; x++)
            if (b.nearest <= depthTiles[y * (uint)occlusion.screen.z + x]) visible = true;
    }
    uint offset = id.x * 5;
    indirectArgs[offset] = b.indexCount;
    indirectArgs[offset + 1] = visible ? 1 : 0;
    indirectArgs[offset + 2] = 0;
    indirectArgs[offset + 3] = 0;
    indirectArgs[offset + 4] = 0;
}
