struct OcclusionUniforms { float4 screen; float4 counts; };
[[vk::binding(0, 0)]] ConstantBuffer<OcclusionUniforms> occlusion;
// The established name requests an unfilterable depth binding in ShaderProgramLoader.
[[vk::binding(1, 0)]] Texture2D<float> prepassDepthTexture;
[[vk::binding(2, 0)]] RWStructuredBuffer<float> depthTiles;
[shader("compute")]
[numthreads(64, 1, 1)]
void reduceDepth(uint3 id : SV_DispatchThreadID)
{
    uint tilesX = (uint)occlusion.screen.z;
    if (id.x >= tilesX * (uint)occlusion.screen.w) return;
    uint2 origin = uint2(id.x % tilesX, id.x / tilesX) * 8;
    float farthest = 0.0;
    for (uint y = origin.y; y < min(origin.y + 8, (uint)occlusion.screen.y); y++)
    for (uint x = origin.x; x < min(origin.x + 8, (uint)occlusion.screen.x); x++)
        farthest = max(farthest, prepassDepthTexture.Load(int3(x, y, 0)));
    depthTiles[id.x] = farthest;
}
